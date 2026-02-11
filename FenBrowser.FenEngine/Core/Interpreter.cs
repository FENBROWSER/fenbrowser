using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using System.Text.RegularExpressions;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;
using FenBrowser.FenEngine.DevTools;
using FenBrowser.FenEngine.Jit;
using FenBrowser.FenEngine.Core.Types;
using FenValue = FenBrowser.FenEngine.Core.FenValue;

namespace FenBrowser.FenEngine.Core
{
    public partial class Interpreter
    {
        private FenObject _stringPrototype;
        private static bool _jitInitialized = false;

        public FenValue Eval(AstNode node, FenEnvironment env, IExecutionContext context)
        {
            // Debugger Hook
            if (node != null && context != null && !string.IsNullOrEmpty(context.CurrentUrl))
            {
                 int line = -1;
                 if (node is Statement s && s.Token != null) line = s.Token.Line;
                 else if (node is Expression e && e.Token != null) line = e.Token.Line;
        
                 // Only pause on valid lines. Optimization: could cache DevTools instance or check a dirty flag
                 if (line > 0 && DevToolsCore.Instance.ShouldPause(context.CurrentUrl, line))
                 {
                     DevToolsCore.Instance.Pause(context.CurrentUrl, line, "breakpoint", env, context);
                 }
            }

            if (node == null) return FenValue.Null;

            switch (node)
            {
                case Program program:
                    return (FenValue)EvalProgram(program, env, context);
                
                case ExpressionStatement exprStmt:
                    return Eval(exprStmt.Expression, env, context);
                
                case IntegerLiteral intLit:
                    return FenValue.FromNumber(intLit.Value);
                
                case DoubleLiteral doubleLit:
                    return FenValue.FromNumber(doubleLit.Value);
                
                case BooleanLiteral boolLit:
                    return FenValue.FromBoolean(boolLit.Value);
                
                case StringLiteral strLit:
                    return FenValue.FromString(strLit.Value);

                case TemplateLiteral tmplLit:
                    return (FenValue)EvalTemplateLiteral(tmplLit, env, context);

                case NullLiteral _:
                    return FenValue.Null;

                case UndefinedLiteral _:
                    return FenValue.Undefined;

                case PrefixExpression prefixExpr:
                    if (prefixExpr.Operator == "++" || prefixExpr.Operator == "--")
                    {
                        return EvalPrefixUpdate(prefixExpr, env, context);
                    }
                    // ES5.1 3A: delete operator must handle property references specially
                    if (prefixExpr.Operator == "delete")
                    {
                        return EvalDeleteExpression(prefixExpr.Right, env, context);
                    }
                    // ES5.1: typeof on undeclared variable should return "undefined", not throw
                    if (prefixExpr.Operator == "typeof" && prefixExpr.Right is Identifier typeofIdent)
                    {
                        try { var resolved = env.Get(typeofIdent.Value); }
                        catch { return FenValue.FromString("undefined"); }
                    }
                    var right = Eval(prefixExpr.Right, env, context);
                    if (IsError(right)) return right;
                    return EvalPrefixExpression(prefixExpr.Operator, right);

                case InfixExpression infixExpr:
                    if (infixExpr.Operator == "++" && infixExpr.Right  == null)
                    {
                        return EvalPostfixUpdate(infixExpr.Left, "++", env, context);
                    }
                    if (infixExpr.Operator == "--" && infixExpr.Right  == null)
                    {
                        return EvalPostfixUpdate(infixExpr.Left, "--", env, context);
                    }
                    var left = Eval(infixExpr.Left, env, context);
                    if (IsError(left)) return left;
                    var rightInfix = Eval(infixExpr.Right, env, context);
                    if (IsError(rightInfix)) return rightInfix;
                    return EvalInfixExpression(infixExpr.Operator, left, rightInfix);

                case BlockStatement blockStmt:
                    return (FenValue)EvalBlockStatement(blockStmt, env, context);

                case IfExpression ifExpr:
                    return (FenValue)EvalIfExpression(ifExpr, env, context);

                case ReturnStatement returnStmt:
                    var val = Eval(returnStmt.ReturnValue, env, context);
                    if (IsError(val)) return val;
                    return FenValue.FromReturnValue(val);

                case FunctionDeclarationStatement funcDeclStmt:
                    // Create function
                    var funcObj = new FenFunction(funcDeclStmt.Function.Parameters, funcDeclStmt.Function.Body, env);
                    funcObj.IsGenerator = funcDeclStmt.Function.IsGenerator;
                    funcObj.IsAsync = funcDeclStmt.Function.IsAsync;
                    if (!string.IsNullOrEmpty(funcDeclStmt.Function.Source))
                        funcObj.Source = funcDeclStmt.Function.Source;
                    
                    var fnVal = FenValue.FromFunction(funcObj);
                    
                    // In strict mode, function declarations in blocks are block-scoped (like let)
                    // In non-strict mode (Annex B), they are function-scoped (var-like) but locally initialized
                    // For now, we update the binding if it exists or set it
                    
                    if (context.StrictMode)
                    {
                        // Strict: Block scoped. Should have been hoisted to block env in EvalBlockStatement
                        env.Set(funcDeclStmt.Function.Name, fnVal);
                    }
                    else
                    {
                        // Sloppy: Update binding. 
                        // If it's a block-level function in sloppy mode, it might hoist to function scope too (Annex B).
                        // For simplicity/robustness, we just Set it in current env (block) and checking Outer if needed?
                        // Actually, just Set in current env is a safe default for now, allowing overwrites.
                        env.Set(funcDeclStmt.Function.Name, fnVal);
                    }
                    return FenValue.Undefined;

                case LetStatement letStmt:
                    var valLet = Eval(letStmt.Value, env, context);
                    if (IsError(valLet)) return valLet;

                    if (letStmt.DestructuringPattern != null)
                    {
                        bool isConstDecl = letStmt.Kind == DeclarationKind.Const;
                        return (FenValue)EvalDestructuringAssignment(letStmt.DestructuringPattern, valLet, env, context, isDeclaration: true, isConst: isConstDecl);
                    }

                    // Use SetConst for const declarations to prevent reassignment
                    if (letStmt.Kind == DeclarationKind.Const)
                        env.SetConst(letStmt.Name.Value, valLet);
                    else
                        env.Set(letStmt.Name.Value, valLet);
                    return valLet;

                case Identifier ident:
                    return (FenValue)EvalIdentifier(ident, env);

                case PrivateIdentifier privateIdent:
                    return (FenValue)EvalPrivateIdentifier(privateIdent, env);

                case FunctionLiteral funcLit:
                    var fenFunc = new FenFunction(funcLit.Parameters, funcLit.Body, env);
                    fenFunc.IsGenerator = funcLit.IsGenerator;
                    // ES2019: Propagate source code
                    if (!string.IsNullOrEmpty(funcLit.Source))
                    {
                        fenFunc.Source = funcLit.Source;
                    }
                    if (funcLit.IsGenerator)
                    {
                        // Return a generator function that creates generator objects when called
                        return FenValue.FromFunction(fenFunc);
                    }
                    return FenValue.FromFunction(fenFunc);

                case MemberExpression memberExpr:
                    return (FenValue)EvalMemberExpression(memberExpr, env, context);

                case TaggedTemplateExpression taggedExpr:
                    return (FenValue)EvalTaggedTemplate(taggedExpr, env, context);

                case IndexExpression indexExpr:
                    return (FenValue)EvalIndexExpression(indexExpr, env, context);

                case NewExpression newExpr:
                    return (FenValue)EvalNewExpression(newExpr, env, context);

                case AssignmentExpression assignExpr:
                    // CallExpression as LHS: evaluate the call, then throw ReferenceError (don't evaluate RHS)
                    if (assignExpr.Left is CallExpression lhsCall)
                    {
                        Eval(lhsCall, env, context); // Evaluate LHS call (side effects)
                        return FenValue.FromError("ReferenceError: Invalid left-hand side in assignment");
                    }

                    // Capture strict unresolved-reference status before evaluating RHS.
                    // In strict mode, an unresolvable identifier reference must still throw even
                    // if RHS side-effects later create a same-named global property.
                    bool strictUnresolvableIdentifierAssignment = false;
                    string strictUnresolvableIdentifierName = null;
                    if (assignExpr.Left is Identifier preLeftIdent &&
                        context.StrictMode &&
                        !env.HasBinding(preLeftIdent.Value))
                    {
                        strictUnresolvableIdentifierAssignment = true;
                        strictUnresolvableIdentifierName = preLeftIdent.Value;
                    }
                    if (assignExpr.Left is Identifier dbgIdent && dbgIdent.Value == "undeclared")
                    {
                        Console.WriteLine($"[DBG-INTERP] strict={context.StrictMode} hasBinding(beforeRhs)={env.HasBinding(dbgIdent.Value)}");
                    }

                    // Evaluate the right side first
                    var value = Eval(assignExpr.Right, env, context);
                    if (IsError(value)) return value;

                    // Handle destructuring assignment
                    if (assignExpr.Left is ArrayLiteral || assignExpr.Left is ObjectLiteral)
                    {
                        return (FenValue)EvalDestructuringAssignment(assignExpr.Left, value, env, context);
                    }
                    
                    // Handle assignment to identifier (var x = ...)
                    if (assignExpr.Left is Identifier leftIdent)
                    {
                        // Strict mode: cannot assign to eval or arguments
                        if (context.StrictMode && (leftIdent.Value == "eval" || leftIdent.Value == "arguments"))
                        {
                            return FenValue.FromError($"SyntaxError: Assignment to '{leftIdent.Value}' in strict mode");
                        }
                        // Strict mode: assignment to unresolvable reference must throw ReferenceError.
                        // Use the pre-RHS captured state so RHS side-effects can't mask the error.
                        if (strictUnresolvableIdentifierAssignment)
                        {
                            return FenValue.FromError($"ReferenceError: {strictUnresolvableIdentifierName} is not defined");
                        }

                        var updateResult = env.Update(leftIdent.Value, value);
                        if (updateResult.Type == JsValueType.Error) return updateResult;
                        return value;
                    }
                    
                    // Handle assignment to private identifier (this.#field = ...)
                    if (assignExpr.Left is PrivateIdentifier leftPrivate)
                    {
                        var thisVal = env.Get("this");
                        if (thisVal  == null || !thisVal.IsObject)
                        {
                            return FenValue.FromError($"Cannot assign to private field '#${leftPrivate.Name}' outside of a class");
                        }
                        var thisObj = thisVal.AsObject();
                        string privateKey = "#" + leftPrivate.Name;
                        thisObj.Set(privateKey, value);
                        return value;
                    }
                    
                    // Handle assignment to member expression (obj.prop = ...)
                    if (assignExpr.Left is MemberExpression leftMember)
                    {
                        var targetObj = Eval(leftMember.Object, env, context);
                        if (IsError(targetObj)) return targetObj;
                        
                        /* [PERF-REMOVED] */

                        if (targetObj.IsObject)
                        {
                            var targetObjVal = targetObj.AsObject();
                            if (targetObjVal != null)
                            {
                            targetObjVal.Set(leftMember.Property, value, context);
                                return value;
                            }
                        }
                        else
                        {
                            /* [PERF-REMOVED] */
                        }
                    }
                    
                    // Handle assignment to index expression (obj[key] = ...)
                    if (assignExpr.Left is IndexExpression leftIndex)
                    {
                        var targetObj = Eval(leftIndex.Left, env, context);
                        if (IsError(targetObj)) return targetObj;
                        
                        var indexVal = Eval(leftIndex.Index, env, context);
                        if (IsError(indexVal)) return indexVal;
                        
                        var key = indexVal.ToString();
                        
                        if (targetObj.IsObject)
                        {
                            var targetObjVal = targetObj.AsObject();
                            if (targetObjVal != null)
                            {
                                targetObjVal.Set(key, value, context);
                                return value;
                            }
                        }
                        else if (targetObj.IsString)
                        {
                            // Strings are immutable, assignment to index is a no-op
                            return value;
                        }
                    }
                    
                    return FenValue.Undefined;

                case CallExpression callExpr:
                    FenValue function = FenValue.Undefined;
                    FenValue thisContext = FenValue.Undefined;

                    if (callExpr.Function is MemberExpression me)
                    {
                        var obj = ToFenValue(Eval(me.Object, env, context));
                        if (IsError(obj)) return obj;
                        
                        try { FenLogger.Debug($"[Interpreter] Resolving call to {me.Property} on object type {obj.Type}", LogCategory.JavaScript); } catch { }

                        
                        // Handle Function.prototype.bind/call/apply
                        if (obj.IsFunction && (me.Property == "bind" || me.Property == "call" || me.Property == "apply"))
                        {
                            var targetFn = obj.AsFunction();
                            var fnArgs = EvalExpressionsWithSpread(callExpr.Arguments, env, context);
                            
                            if (me.Property == "bind")
                            {
                                // bind(thisArg, ...args) - Returns a bound function
                                var boundThis = fnArgs.Count > 0 ? fnArgs[0] : FenValue.Undefined;
                                var boundArgs = fnArgs.Count > 1 ? fnArgs.Skip(1).ToList() : new List<FenValue>();
                                
                                var boundFn = new FenFunction("bound " + targetFn.Name, (invokeArgs, _) =>
                                {
                                    var allArgs = new List<FenValue>(boundArgs);
                                    allArgs.AddRange(invokeArgs);
                                    return targetFn.Invoke(allArgs.ToArray(), context);
                                });
                                
                                return FenValue.FromFunction(boundFn);
                            }
                            else if (me.Property == "call")
                            {
                                // call(thisArg, ...args) - Invokes with thisArg
                                var callThis = fnArgs.Count > 0 ? fnArgs[0] : FenValue.Undefined;
                                var callArgs = fnArgs.Count > 1 ? fnArgs.Skip(1).ToArray() : new FenValue[0];
                                
                                return ApplyFunction(obj, callArgs.ToList(), context, callThis);
                            }
                            else if (me.Property == "apply")
                            {
                                // apply(thisArg, argsArray) - Invokes with thisArg and args from array
                                var applyThis = fnArgs.Count > 0 ? fnArgs[0] : FenValue.Undefined;
                                var applyArgs = new List<FenValue>();
                                
                                if (fnArgs.Count > 1 && fnArgs[1].IsObject)
                                {
                                    var argsArray = fnArgs[1].AsObject();
                                    var lenVal = argsArray?.Get("length", context);
                                    int len = (lenVal != null && lenVal.Value.IsNumber) ? (int)lenVal.Value.ToNumber() : 0;
                                    for (int i = 0; i < len; i++)
                                    {
                                        var argVal = argsArray.Get(i.ToString(), context);
                                        applyArgs.Add(argVal );
                                    }
                                }
                                
                                return ApplyFunction(obj, applyArgs, context, applyThis);
                            }
                        }
                        
                        {
                            thisContext = obj;
                            
                            // Resolve property on the object
                            if (obj.IsObject)
                            {
                                var o = obj.AsObject();
                                if (o != null) function = o.Get(me.Property, context);
                            }
                            else if (obj.IsString)
                            {
                                // String methods lookup (not length, as it is a property, but methods like substring if we had them)
                                var proto = GetStringPrototype();
                                function = proto?.Get(me.Property, context) ?? FenValue.Undefined;
                            }
                            else if (obj.IsNumber)
                            {
                                // Number methods lookup (toFixed, toPrecision, toExponential, toString)
                                var proto = GetNumberPrototype();
                                function = proto?.Get(me.Property, context) ?? FenValue.Undefined;
                            }
                            else if (obj.IsBigInt)
                            {
                                // BigInt methods lookup (toString, valueOf)
                                if (me.Property == "toString")
                                {
                                    function = FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
                                    {
                                        var bigInt = obj.AsBigInt();
                                        if (bigInt == null) return FenValue.FromString("0");
                                        return FenValue.FromString(bigInt.ToStringWithoutSuffix());
                                    }));
                                }
                                else if (me.Property == "valueOf")
                                {
                                    function = FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
                                    {
                                        return obj;
                                    }));
                                }
                                else
                                {
                                    function = FenValue.Undefined;
                                }
                            }

                            if (function.IsUndefined && function.IsNull) function = FenValue.Undefined;
                        }
                    }
                    else
                    {
                        function = ToFenValue(Eval(callExpr.Function, env, context));
                    }

                    if (function.Type == JsValueType.Error) return (FenValue)function;
                    
                    var args = EvalExpressionsWithSpread(callExpr.Arguments, env, context);
                    if (args.Count == 1 && IsError(args[0])) return args[0];

                    return ApplyFunction((FenValue)function, args, context, thisContext);

                case TryStatement tryStmt:
                    return ToFenValue(EvalTryStatement(tryStmt, env, context));

                case ThrowStatement throwStmt:
                    return ToFenValue(EvalThrowStatement(throwStmt, env, context));

                case WhileStatement whileStmt:
                    return ToFenValue(EvalWhileStatement(whileStmt, env, context));

                case ForStatement forStmt:
                    return ToFenValue(EvalForStatement(forStmt, env, context));

                case ArrayLiteral arrayLit:
                    return ToFenValue(EvalArrayLiteral(arrayLit, env, context));

                case ObjectLiteral objectLit:
                    return ToFenValue(EvalObjectLiteral(objectLit, env, context));

                // Ternary conditional: condition ? consequent : alternate
                case ConditionalExpression condExpr:
                    return ToFenValue(EvalConditionalExpression(condExpr, env, context));

                case ClassStatement classStmt:
                    return (FenValue)EvalClassStatement(classStmt, env, context);

                case ImportDeclaration importDecl:
                    return (FenValue)EvalImportDeclaration(importDecl, env, context);

                case ExportDeclaration exportDecl:
                    return (FenValue)EvalExportDeclaration(exportDecl, env, context);

                // Arrow function: (params) => body
                case ArrowFunctionExpression arrowExpr:
                    return (FenValue)EvalArrowFunctionExpression(arrowExpr, env);

                case AsyncFunctionExpression asyncExpr:
                    return (FenValue)EvalAsyncFunctionExpression(asyncExpr, env);

                case AwaitExpression awaitExpr:
                    return (FenValue)EvalAwaitExpression(awaitExpr, env, context);

                case RegexLiteral regexLit:
                    return (FenValue)EvalRegexLiteral(regexLit, env);

                // ES2020: import.meta
                case ImportMetaExpression importMeta:
                    // Return a meta object with url property
                    // In a real environment, this would depend on the current module's URL
                    var metaObj = new FenObject();
                    metaObj.Set("url", FenValue.FromString("file:///local/script.js"), null); // Placeholder URL
                    return FenValue.FromObject(metaObj);

                // for-in loop: for (x in obj) { ... }
                case ForInStatement forInStmt:
                    return (FenValue)EvalForInStatement(forInStmt, env, context);

                // for-of loop: for (x of iterable) { ... }
                case ForOfStatement forOfStmt:
                    return (FenValue)EvalForOfStatement(forOfStmt, env, context);



                // Empty expression (for recovery)
                case EmptyExpression:
                    return FenValue.Undefined;

                // Switch statement
                case SwitchStatement switchStmt:
                    return (FenValue)EvalSwitchStatement(switchStmt, env, context);

                // Break statement
                case BreakStatement breakStmt:
                    return breakStmt.Label != null
                        ? FenValue.BreakWithLabel(breakStmt.Label.Value)
                        : FenValue.Break;

                // Continue statement
                case ContinueStatement continueStmt:
                    return continueStmt.Label != null
                        ? FenValue.ContinueWithLabel(continueStmt.Label.Value)
                        : FenValue.Continue;

                // Labeled statement
                case LabeledStatement labeledStmt:
                    return (FenValue)EvalLabeledStatement(labeledStmt, env, context);

                // Do-while loop
                case DoWhileStatement doWhileStmt:
                    return (FenValue)EvalDoWhileStatement(doWhileStmt, env, context);
                
                // Yield expression (for generator functions)
                case YieldExpression yieldExpr:
                    return (FenValue)EvalYieldExpression(yieldExpr, env, context);

                // ES6+ Optional chaining: obj?.prop
                case OptionalChainExpression optChainExpr:
                    return (FenValue)EvalOptionalChainExpression(optChainExpr, env, context);

                // ES6+ Nullish coalescing: a ?? b
                case NullishCoalescingExpression nullishExpr:
                    return (FenValue)EvalNullishCoalescingExpression(nullishExpr, env, context);

                // ES6+ Logical assignment: a ||= b, a &&= b, a ??= b
                case LogicalAssignmentExpression logicalAssignExpr:
                    return (FenValue)EvalLogicalAssignment(logicalAssignExpr, env, context);

                // ES6+ Exponentiation: a ** b
                case ExponentiationExpression expExpr:
                    return (FenValue)EvalExponentiationExpression(expExpr, env, context);

                // ES6+ Bitwise NOT: ~x
                case BitwiseNotExpression bitwiseNotExpr:
                    return (FenValue)EvalBitwiseNotExpression(bitwiseNotExpr, env, context);

                // ES6+ BigInt literal: 123n
                case BigIntLiteral bigIntLit:
                    return (FenValue)EvalBigIntLiteral(bigIntLit);

                // ES6+ Compound assignment: a += b, a **= b, etc.
                case CompoundAssignmentExpression compoundExpr:
                    return (FenValue)EvalCompoundAssignmentExpression(compoundExpr, env, context);

                // ES2015: new.target meta property
                case NewTargetExpression newTarget:
                    return context.NewTarget;

                // ES5.1 with statement: with (obj) { ... }
                case WithStatement withStmt:
                    return (FenValue)EvalWithStatement(withStmt, env, context);

            }

            return FenValue.Undefined;
        }
        
        /// <summary>
        /// Evaluate yield expression - returns a YieldValue for generator iteration
        /// For yield*, delegates to inner generator/iterable and yields each value
        /// </summary>
        private IValue EvalYieldExpression(YieldExpression yieldExpr, FenEnvironment env, IExecutionContext context)
        {
            var value = yieldExpr.Value != null ? Eval(yieldExpr.Value, env, context) : FenValue.Undefined;
            
            // ES2015 yield* delegation: iterate inner generator/iterable
            if (yieldExpr.Delegate && value.IsObject)
            {
                // Return a special delegation marker that CreateGeneratorObject will handle
                return FenValue.FromYieldDelegate(value);
            }
            
            return FenValue.FromYield(value);
        }

        private IValue EvalArrayLiteral(ArrayLiteral node, FenEnvironment env, IExecutionContext context)
        {
            var elements = EvalExpressionsWithSpread(node.Elements, env, context);
            if (elements.Count == 1 && IsError(elements[0])) return elements[0];
            
            var arrayObj = new FenObject();
            arrayObj.InternalClass = "Array"; // ES5.1: [[Class]] = "Array"
            for (int i = 0; i < elements.Count; i++)
            {
                arrayObj.Set(i.ToString(), elements[i], context);
            }
            arrayObj.Set("length", FenValue.FromNumber(elements.Count), context);
            arrayObj.SetPrototype(GetArrayPrototype());
            
            return FenValue.FromObject(arrayObj);
        }

        private FenObject _arrayPrototype;
        private FenObject GetArrayPrototype()
        {
            if (_arrayPrototype != null) return _arrayPrototype;
            _arrayPrototype = new FenObject();
            FenObject.DefaultArrayPrototype = _arrayPrototype; // Share with FenRuntime for array creation

            // Array.prototype.push
            _arrayPrototype.Set("push", FenValue.FromFunction(new FenFunction("push", (FenValue[] args, FenValue thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) 
                {
                    /* [PERF-REMOVED] */
                    return FenValue.FromNumber(0);
                }
                
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                /* [PERF-REMOVED] */

                foreach (var arg in args)
                {
                    obj.Set(len.ToString(), arg, null);
                    len++;
                }
                
                obj.Set("length", FenValue.FromNumber(len), null);
                return FenValue.FromNumber(len);
            })));

            // Array.prototype.join
            _arrayPrototype.Set("join", FenValue.FromFunction(new FenFunction("join", (FenValue[] args, FenValue thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return FenValue.FromString("");
                
                var separator = args.Length > 0 ? args[0].ToString() : ",";
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++)
                {
                    if (i > 0) sb.Append(separator);
                    var val = obj.Get(i.ToString(), null);
                    if (val != null && !val.IsUndefined && !val == null)
                    {
                        sb.Append(val.ToString());
                    }
                }
                
                return FenValue.FromString(sb.ToString());
            })));

            // Array.prototype.pop()
            _arrayPrototype.Set("pop", FenValue.FromFunction(new FenFunction("pop", (FenValue[] args, FenValue thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                if (len == 0) return FenValue.Undefined;
                var lastIdx = (len - 1).ToString();
                var val = obj.Get(lastIdx, null);
                obj.Delete(lastIdx, null);
                obj.Set("length", FenValue.FromNumber(len - 1), null);
                return val ;
            })));

            // Array.prototype.shift()
            _arrayPrototype.Set("shift", FenValue.FromFunction(new FenFunction("shift", (FenValue[] args, FenValue thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                if (len == 0) return FenValue.Undefined;
                var first = obj.Get("0", null);
                for (int i = 1; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    obj.Set((i - 1).ToString(), val , null);
                }
                obj.Delete((len - 1).ToString(), null);
                obj.Set("length", FenValue.FromNumber(len - 1), null);
                return first ;
            })));

            // Array.prototype.unshift(...elements)
            _arrayPrototype.Set("unshift", FenValue.FromFunction(new FenFunction("unshift", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return FenValue.FromNumber(0);
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                int argCount = args.Length;
                // Shift existing elements
                for (int i = len - 1; i >= 0; i--)
                {
                    var val = obj.Get(i.ToString(), null);
                    obj.Set((i + argCount).ToString(), val , null);
                }
                // Insert new elements
                for (int i = 0; i < argCount; i++)
                {
                    obj.Set(i.ToString(), args[i], null);
                }
                obj.Set("length", FenValue.FromNumber(len + argCount), null);
                return FenValue.FromNumber(len + argCount);
            })));

            // Array.prototype.slice(start, end)
            _arrayPrototype.Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return FenValue.FromObject(new FenObject());
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int end = args.Length > 1 ? (int)args[1].ToNumber() : len;
                if (start < 0) start = Math.Max(len + start, 0);
                if (end < 0) end = Math.Max(len + end, 0);
                start = Math.Min(start, len);
                end = Math.Min(end, len);

                var result = FenObject.CreateArray();
                int idx = 0;
                for (int i = start; i < end; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    result.Set(idx.ToString(), val , null);
                    idx++;
                }
                result.Set("length", FenValue.FromNumber(idx), null);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.splice(start, deleteCount, ...items)
            _arrayPrototype.Set("splice", FenValue.FromFunction(new FenFunction("splice", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return FenValue.FromObject(new FenObject());
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (start < 0) start = Math.Max(len + start, 0);
                start = Math.Min(start, len);
                
                int deleteCount = args.Length > 1 ? (int)args[1].ToNumber() : len - start;
                deleteCount = Math.Max(0, Math.Min(deleteCount, len - start));

                var deleted = FenObject.CreateArray();
                for (int i = 0; i < deleteCount; i++)
                {
                    var val = obj.Get((start + i).ToString(), null);
                    deleted.Set(i.ToString(), val , null);
                }
                deleted.Set("length", FenValue.FromNumber(deleteCount), null);
                
                var insertItems = args.Skip(2).ToArray();
                int insertCount = insertItems.Length;
                int shift = insertCount - deleteCount;
                
                if (shift > 0)
                {
                    for (int i = len - 1; i >= start + deleteCount; i--)
                    {
                        var val = obj.Get(i.ToString(), null);
                        obj.Set((i + shift).ToString(), val , null);
                    }
                }
                else if (shift < 0)
                {
                    for (int i = start + deleteCount; i < len; i++)
                    {
                        var val = obj.Get(i.ToString(), null);
                        obj.Set((i + shift).ToString(), val , null);
                    }
                    for (int i = len + shift; i < len; i++)
                    {
                        obj.Delete(i.ToString(), null);
                    }
                }
                
                for (int i = 0; i < insertCount; i++)
                {
                    obj.Set((start + i).ToString(), insertItems[i], null);
                }
                obj.Set("length", FenValue.FromNumber(len + shift), null);
                
                return FenValue.FromObject(deleted);
            })));

            // Array.prototype.concat(...arrays)
            _arrayPrototype.Set("concat", FenValue.FromFunction(new FenFunction("concat", (args, thisVal) =>
            {
                var result = FenObject.CreateArray();
                int idx = 0;
                
                var obj = thisVal.AsObject();
                if (obj != null)
                {
                    var lenVal = obj.Get("length", null);
                    var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                    for (int i = 0; i < len; i++)
                    {
                        result.Set(idx.ToString(), obj.Get(i.ToString(), null) , null);
                        idx++;
                    }
                }
                
                foreach (var arg in args)
                {
                    if (arg.IsObject)
                    {
                        var arrObj = arg.AsObject();
                        var arrLen = arrObj.Get("length", null);
                        if (arrLen != null && arrLen.IsNumber)
                        {
                            int len = (int)arrLen.ToNumber();
                            for (int i = 0; i < len; i++)
                            {
                                result.Set(idx.ToString(), arrObj.Get(i.ToString(), null) , null);
                                idx++;
                            }
                        }
                        else
                        {
                            result.Set(idx.ToString(), arg, null);
                            idx++;
                        }
                    }
                    else
                    {
                        result.Set(idx.ToString(), arg, null);
                        idx++;
                    }
                }
                
                result.Set("length", FenValue.FromNumber(idx), null);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.indexOf(searchElement, fromIndex)
            _arrayPrototype.Set("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.FromNumber(-1);
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var search = args[0];
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (from < 0) from = Math.Max(len + from, 0);
                
                for (int i = from; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    if (val != null && val.StrictEquals(search)) return FenValue.FromNumber(i);
                }
                return FenValue.FromNumber(-1);
            })));

            // Array.prototype.lastIndexOf(searchElement, fromIndex)
            _arrayPrototype.Set("lastIndexOf", FenValue.FromFunction(new FenFunction("lastIndexOf", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.FromNumber(-1);
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var search = args[0];
                int from = args.Length > 1 ? (int)args[1].ToNumber() : len - 1;
                if (from < 0) from = len + from;
                from = Math.Min(from, len - 1);
                
                for (int i = from; i >= 0; i--)
                {
                    var val = obj.Get(i.ToString(), null);
                    if (val != null && val.StrictEquals(search)) return FenValue.FromNumber(i);
                }
                return FenValue.FromNumber(-1);
            })));

            // Array.prototype.includes(searchElement, fromIndex)
            _arrayPrototype.Set("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.FromBoolean(false);
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var search = args[0];
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (from < 0) from = Math.Max(len + from, 0);
                
                for (int i = from; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    if (val != null && (val.StrictEquals(search) || (search.IsNumber && double.IsNaN(search.ToNumber()) && val.IsNumber && double.IsNaN(val.ToNumber()))))
                        return FenValue.FromBoolean(true);
                }
                return FenValue.FromBoolean(false);
            })));

            // Array.prototype.reverse()
            _arrayPrototype.Set("reverse", FenValue.FromFunction(new FenFunction("reverse", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return thisVal;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = 0; i < len / 2; i++)
                {
                    var left = obj.Get(i.ToString(), null);
                    var right = obj.Get((len - 1 - i).ToString(), null);
                    obj.Set(i.ToString(), right , null);
                    obj.Set((len - 1 - i).ToString(), left , null);
                }
                return thisVal;
            })));

            // Array.prototype.sort(compareFn)
            _arrayPrototype.Set("sort", FenValue.FromFunction(new FenFunction("sort", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return thisVal;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                if (len <= 1) return thisVal;
                
                var items = new List<FenValue>();
                for (int i = 0; i < len; i++)
                {
                    items.Add(obj.Get(i.ToString(), null) );
                }
                
                FenFunction compareFn = args.Length > 0 ? args[0].AsFunction() : null;
                
                items.Sort((a, b) =>
                {
                    if (compareFn != null)
                    {
                        var result = compareFn.Invoke(new FenValue[] { a, b }, null);
                        return (int)result.ToNumber();
                    }
                    return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
                });
                
                for (int i = 0; i < len; i++)
                {
                    obj.Set(i.ToString(), items[i], null);
                }
                return thisVal;
            })));

            // Array.prototype.forEach(callback, thisArg)
            _arrayPrototype.Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.Undefined;
                var callback = args[0].AsFunction();
                if (callback  == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
                
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    callback.Invoke(new FenValue[] { val , FenValue.FromNumber(i), thisVal }, null);
                }
                return FenValue.Undefined;
            })));

            // Array.prototype.map(callback, thisArg)
            _arrayPrototype.Set("map", FenValue.FromFunction(new FenFunction("map", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                var result = FenObject.CreateArray();
                if (obj  == null || args.Length == 0)
                {
                    result.Set("length", FenValue.FromNumber(0), null);
                    return FenValue.FromObject(result);
                }
                var callback = args[0].AsFunction();
                if (callback  == null)
                {
                    result.Set("length", FenValue.FromNumber(0), null);
                    return FenValue.FromObject(result);
                }
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    var mapped = callback.Invoke(new FenValue[] { val , FenValue.FromNumber(i), thisVal }, null);
                    result.Set(i.ToString(), mapped, null);
                }
                result.Set("length", FenValue.FromNumber(len), null);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.flat(depth)
            _arrayPrototype.Set("flat", FenValue.FromFunction(new FenFunction("flat", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return FenValue.FromObject(FenObject.CreateArray());
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var depth = args.Length > 0 ? args[0].ToNumber() : 1;

                var result = FenObject.CreateArray();
                int idx = 0;
                
                void Flatten(IObject source, int sourceLen, double currentDepth)
                {
                    for (int i = 0; i < sourceLen; i++)
                    {
                        var val = source.Get(i.ToString(), null);
                        if (val  == null || val.IsUndefined) continue; // Skip empty slots
                        
                        // Check if array (has length and is object)
                        var isArr = val.IsObject && val.AsObject().Get("length", null).IsNumber; // Simplified check
                        // Better check: is it Array constructor instance? Difficult without access to global Array. 
                        // Duck typing "length" property is often used in simplified engines.
                        
                        if (currentDepth > 0 && isArr)
                        {
                            var subObj = val.AsObject();
                            var subLen = (int)subObj.Get("length", null).ToNumber();
                            Flatten(subObj, subLen, currentDepth - 1);
                        }
                        else
                        {
                            result.Set(idx.ToString(), val, null);
                            idx++;
                        }
                    }
                }
                
                Flatten(obj, len, depth);
                result.Set("length", FenValue.FromNumber(idx), null);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.flatMap(callback, thisArg)
            _arrayPrototype.Set("flatMap", FenValue.FromFunction(new FenFunction("flatMap", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0 || !args[0].IsFunction) return FenValue.FromObject(FenObject.CreateArray());

                var callback = args[0].AsFunction();
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;

                var result = FenObject.CreateArray();
                int idx = 0;
                
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    var mapped = callback.Invoke(new FenValue[] { val , FenValue.FromNumber(i), thisVal }, null);
                    
                    // Flatten 1 level
                     var isArr = mapped.IsObject && mapped.AsObject().Get("length", null).IsNumber;
                     if (isArr)
                     {
                        var subObj = mapped.AsObject();
                        var subLen = (int)subObj.Get("length", null).ToNumber();
                        for (int k = 0; k < subLen; k++)
                        {
                            var subVal = subObj.Get(k.ToString(), null);
                            if (subVal != null && !subVal.IsUndefined)
                            {
                                result.Set(idx.ToString(), subVal, null);
                                idx++;
                            }
                        }
                     }
                     else
                     {
                        result.Set(idx.ToString(), mapped, null);
                        idx++;
                     }
                }
                
                result.Set("length", FenValue.FromNumber(idx), null);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.fill(value, start, end)
            _arrayPrototype.Set("fill", FenValue.FromFunction(new FenFunction("fill", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return thisVal;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var start = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                var end = args.Length > 2 ? (int)args[2].ToNumber() : len;
                
                if (start < 0) start = Math.Max(len + start, 0);
                if (end < 0) end = Math.Max(len + end, 0);
                start = Math.Min(start, len);
                end = Math.Min(end, len);
                
                for (int i = start; i < end; i++)
                {
                    obj.Set(i.ToString(), value, null);
                }
                return thisVal;
            })));

            // Array.prototype.filter(callback, thisArg)
            _arrayPrototype.Set("filter", FenValue.FromFunction(new FenFunction("filter", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                var result = FenObject.CreateArray();
                int idx = 0;
                if (obj  == null || args.Length == 0)
                {
                    result.Set("length", FenValue.FromNumber(0), null);
                    return FenValue.FromObject(result);
                }
                var callback = args[0].AsFunction();
                if (callback  == null)
                {
                    result.Set("length", FenValue.FromNumber(0), null);
                    return FenValue.FromObject(result);
                }
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    var keep = callback.Invoke(new FenValue[] { val , FenValue.FromNumber(i), thisVal }, null);
                    if (keep.ToBoolean())
                    {
                        result.Set(idx.ToString(), val , null);
                        idx++;
                    }
                }
                result.Set("length", FenValue.FromNumber(idx), null);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.reduce(callback, initialValue)
            _arrayPrototype.Set("reduce", FenValue.FromFunction(new FenFunction("reduce", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.Undefined;
                var callback = args[0].AsFunction();
                if (callback  == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                int start = 0;
                IValue accumulator;
                if (args.Length > 1)
                {
                    accumulator = args[1];
                }
                else
                {
                    if (len == 0) return FenValue.FromError("Reduce of empty array with no initial value");
                    accumulator = obj.Get("0", null) ;
                    start = 1;
                }
                
                for (int i = start; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    accumulator = ToFenValue(callback.Invoke(new FenValue[] { ToFenValue(accumulator), val , FenValue.FromNumber(i), thisVal }, null));
                }
                return (FenValue)accumulator;
            })));

            // Array.prototype.reduceRight(callback, initialValue)
            _arrayPrototype.Set("reduceRight", FenValue.FromFunction(new FenFunction("reduceRight", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.Undefined;
                var callback = args[0].AsFunction();
                if (callback  == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                int start = len - 1;
                IValue accumulator;
                if (args.Length > 1)
                {
                    accumulator = args[1];
                }
                else
                {
                    if (len == 0) return FenValue.FromError("Reduce of empty array with no initial value");
                    accumulator = obj.Get((len - 1).ToString(), null) ;
                    start = len - 2;
                }
                
                for (int i = start; i >= 0; i--)
                {
                    var val = obj.Get(i.ToString(), null);
                    accumulator = ToFenValue(callback.Invoke(new FenValue[] { ToFenValue(accumulator), val , FenValue.FromNumber(i), thisVal }, null));
                }
                return (FenValue)accumulator;
            })));

            // Array.prototype.find(callback, thisArg)
            _arrayPrototype.Set("find", FenValue.FromFunction(new FenFunction("find", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.Undefined;
                var callback = args[0].AsFunction();
                if (callback  == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null) ;
                    var result = callback.Invoke(new FenValue[] { val, FenValue.FromNumber(i), thisVal }, null);
                    if (result.ToBoolean()) return val;
                }
                return FenValue.Undefined;
            })));

            // Array.prototype.findIndex(callback, thisArg)
            _arrayPrototype.Set("findIndex", FenValue.FromFunction(new FenFunction("findIndex", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.FromNumber(-1);
                var callback = args[0].AsFunction();
                if (callback  == null) return FenValue.FromNumber(-1);
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null) ;
                    var result = callback.Invoke(new FenValue[] { val, FenValue.FromNumber(i), thisVal }, null);
                    if (result.ToBoolean()) return FenValue.FromNumber(i);
                }
                return FenValue.FromNumber(-1);
            })));

            // Array.prototype.some(callback, thisArg)
            _arrayPrototype.Set("some", FenValue.FromFunction(new FenFunction("some", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.FromBoolean(false);
                var callback = args[0].AsFunction();
                if (callback  == null) return FenValue.FromBoolean(false);
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null) ;
                    var result = callback.Invoke(new FenValue[] { val, FenValue.FromNumber(i), thisVal }, null);
                    if (result.ToBoolean()) return FenValue.FromBoolean(true);
                }
                return FenValue.FromBoolean(false);
            })));

            // Array.prototype.flat(depth)
            _arrayPrototype.Set("flat", FenValue.FromFunction(new FenFunction("flat", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return FenValue.FromObject(CreateArray(new string[0]));
                int depth = args.Length > 0 ? (int)args[0].ToNumber() : 1;
                var result = FlattenArray(obj, depth);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.flatMap(callback, thisArg)
            _arrayPrototype.Set("flatMap", FenValue.FromFunction(new FenFunction("flatMap", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.FromObject(CreateArray(new string[0]));
                var callback = args[0].AsFunction();
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
                
                // Map
                var mapped = new FenObject();
                var lenVal = obj.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null);
                    var res = callback.Invoke(new FenValue[] { val , FenValue.FromNumber(i), thisVal }, null);
                    mapped.Set(i.ToString(), res, null);
                }
                mapped.Set("length", FenValue.FromNumber(len), null);
                
                // Flat(1)
                return FenValue.FromObject(FlattenArray(mapped, 1));
            })));

            // Array.prototype.fill(value, start, end)
            _arrayPrototype.Set("fill", FenValue.FromFunction(new FenFunction("fill", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null) return thisVal;
                var value = args.Length > 0 ? args[0] : FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                int start = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int end = args.Length > 2 ? (int)args[2].ToNumber() : len;
                
                if (start < 0) start = Math.Max(len + start, 0);
                if (end < 0) end = Math.Max(len + end, 0);
                start = Math.Min(start, len);
                end = Math.Min(end, len);
                
                for (int i = start; i < end; i++)
                {
                    obj.Set(i.ToString(), value, null);
                }
                return thisVal;
            })));

            // Array.prototype.copyWithin(target, start[, end]) - ES2015
            _arrayPrototype.Set("copyWithin", FenValue.FromFunction(new FenFunction("copyWithin", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return thisVal;
                var lenVal = obj.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                int to = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int final = args.Length > 2 ? (int)args[2].ToNumber() : len;
                if (to < 0) to = Math.Max(len + to, 0); else to = Math.Min(to, len);
                if (from < 0) from = Math.Max(len + from, 0); else from = Math.Min(from, len);
                if (final < 0) final = Math.Max(len + final, 0); else final = Math.Min(final, len);
                int count = Math.Min(final - from, len - to);
                var temp = new FenValue[Math.Max(0, count)];
                for (int i = 0; i < count; i++) temp[i] = obj.Get((from + i).ToString(), null);
                for (int i = 0; i < count; i++) obj.Set((to + i).ToString(), temp[i], null);
                return thisVal;
            })));

            // Array.prototype.every(callback, thisArg)
            _arrayPrototype.Set("every", FenValue.FromFunction(new FenFunction("every", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj  == null || args.Length == 0) return FenValue.FromBoolean(true);
                var callback = args[0].AsFunction();
                if (callback  == null) return FenValue.FromBoolean(true);
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get(i.ToString(), null) ;
                    var result = callback.Invoke(new FenValue[] { val, FenValue.FromNumber(i), thisVal }, null);
                    if (!result.ToBoolean()) return FenValue.FromBoolean(false);
                }
                return FenValue.FromBoolean(true);
            })));

            // Array.prototype.findLast(callback, thisArg) - ES2023
            _arrayPrototype.Set("findLast", FenValue.FromFunction(new FenFunction("findLast", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null || args.Length == 0) return FenValue.Undefined;
                var callback = args[0].AsFunction();
                if (callback == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = len - 1; i >= 0; i--)
                {
                    var val = obj.Get(i.ToString(), null);
                    var result = callback.Invoke(new FenValue[] { val, FenValue.FromNumber(i), thisVal }, null);
                    if (result.ToBoolean()) return val;
                }
                return FenValue.Undefined;
            })));

            // Array.prototype.findLastIndex(callback, thisArg) - ES2023
            _arrayPrototype.Set("findLastIndex", FenValue.FromFunction(new FenFunction("findLastIndex", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null || args.Length == 0) return FenValue.FromNumber(-1);
                var callback = args[0].AsFunction();
                if (callback == null) return FenValue.FromNumber(-1);
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                for (int i = len - 1; i >= 0; i--)
                {
                    var val = obj.Get(i.ToString(), null);
                    var result = callback.Invoke(new FenValue[] { val, FenValue.FromNumber(i), thisVal }, null);
                    if (result.ToBoolean()) return FenValue.FromNumber(i);
                }
                return FenValue.FromNumber(-1);
            })));

            // Array.prototype.at(index) - ES2022
            _arrayPrototype.Set("at", FenValue.FromFunction(new FenFunction("at", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                int index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0) index = len + index;
                if (index < 0 || index >= len) return FenValue.Undefined;
                
                return obj.Get(index.ToString(), null);
            })));

            // Array.prototype.toSorted(compareFn) - ES2023
            _arrayPrototype.Set("toSorted", FenValue.FromFunction(new FenFunction("toSorted", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.FromObject(new FenObject());
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                var items = new List<FenValue>();
                for (int i = 0; i < len; i++)
                {
                    items.Add(obj.Get(i.ToString(), null));
                }
                
                FenFunction compareFn = args.Length > 0 ? args[0].AsFunction() : null;
                
                items.Sort((a, b) =>
                {
                    if (compareFn != null)
                    {
                        var result = compareFn.Invoke(new FenValue[] { a, b }, null);
                        return (int)result.ToNumber();
                    }
                    return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
                });
                
                var result = new FenObject();
                for (int i = 0; i < items.Count; i++)
                {
                    result.Set(i.ToString(), items[i], null);
                }
                result.Set("length", FenValue.FromNumber(items.Count), null);
                result.SetPrototype(_arrayPrototype);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.toReversed() - ES2023
            _arrayPrototype.Set("toReversed", FenValue.FromFunction(new FenFunction("toReversed", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.FromObject(new FenObject());
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                var result = new FenObject();
                for (int i = 0; i < len; i++)
                {
                    var val = obj.Get((len - 1 - i).ToString(), null);
                    result.Set(i.ToString(), val, null);
                }
                result.Set("length", FenValue.FromNumber(len), null);
                result.SetPrototype(_arrayPrototype);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.with(index, value) - ES2023
            _arrayPrototype.Set("with", FenValue.FromFunction(new FenFunction("with", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.FromObject(new FenObject());
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                int index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                var value = args.Length > 1 ? args[1] : FenValue.Undefined;
                if (index < 0) index = len + index;
                
                var result = new FenObject();
                for (int i = 0; i < len; i++)
                {
                    if (i == index)
                    {
                        result.Set(i.ToString(), value, null);
                    }
                    else
                    {
                        result.Set(i.ToString(), obj.Get(i.ToString(), null), null);
                    }
                }
                result.Set("length", FenValue.FromNumber(len), null);
                result.SetPrototype(_arrayPrototype);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.toSpliced(start, deleteCount, ...items) - ES2023 change-by-copy
            _arrayPrototype.Set("toSpliced", FenValue.FromFunction(new FenFunction("toSpliced", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.FromObject(new FenObject());
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;

                int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (start < 0) start = Math.Max(len + start, 0);
                else start = Math.Min(start, len);

                int deleteCount = args.Length > 1 ? (int)args[1].ToNumber() : len - start;
                deleteCount = Math.Max(0, Math.Min(deleteCount, len - start));

                var items = new List<FenValue>();
                for (int i = 2; i < args.Length; i++) items.Add(args[i]);

                var result = new FenObject();
                int ri = 0;
                for (int i = 0; i < start; i++)
                    result.Set((ri++).ToString(), obj.Get(i.ToString(), null), null);
                foreach (var item in items)
                    result.Set((ri++).ToString(), item, null);
                for (int i = start + deleteCount; i < len; i++)
                    result.Set((ri++).ToString(), obj.Get(i.ToString(), null), null);
                result.Set("length", FenValue.FromNumber(ri), null);
                result.SetPrototype(_arrayPrototype);
                return FenValue.FromObject(result);
            })));

            // Array.prototype.entries() - ES2015 iterator
            _arrayPrototype.Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                int index = 0;
                
                var iterator = new FenObject();
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    if (index >= len)
                    {
                        var doneResult = new FenObject();
                        doneResult.Set("done", FenValue.FromBoolean(true), null);
                        doneResult.Set("value", FenValue.Undefined, null);
                        return FenValue.FromObject(doneResult);
                    }
                    var pair = new FenObject();
                    pair.Set("0", FenValue.FromNumber(index), null);
                    pair.Set("1", obj.Get(index.ToString(), null), null);
                    pair.Set("length", FenValue.FromNumber(2), null);
                    
                    var iterResult = new FenObject();
                    iterResult.Set("done", FenValue.FromBoolean(false), null);
                    iterResult.Set("value", FenValue.FromObject(pair), null);
                    index++;
                    return FenValue.FromObject(iterResult);
                })));
                iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iterator))));
                return FenValue.FromObject(iterator);
            })));

            // Array.prototype.keys() - ES2015 iterator
            _arrayPrototype.Set("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                int index = 0;
                
                var iterator = new FenObject();
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    if (index >= len)
                    {
                        var doneResult = new FenObject();
                        doneResult.Set("done", FenValue.FromBoolean(true), null);
                        doneResult.Set("value", FenValue.Undefined, null);
                        return FenValue.FromObject(doneResult);
                    }
                    var iterResult = new FenObject();
                    iterResult.Set("done", FenValue.FromBoolean(false), null);
                    iterResult.Set("value", FenValue.FromNumber(index), null);
                    index++;
                    return FenValue.FromObject(iterResult);
                })));
                iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iterator))));
                return FenValue.FromObject(iterator);
            })));

            // Array.prototype.values() - ES2015 iterator
            _arrayPrototype.Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                int index = 0;
                
                var iterator = new FenObject();
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    if (index >= len)
                    {
                        var doneResult = new FenObject();
                        doneResult.Set("done", FenValue.FromBoolean(true), null);
                        doneResult.Set("value", FenValue.Undefined, null);
                        return FenValue.FromObject(doneResult);
                    }
                    var iterResult = new FenObject();
                    iterResult.Set("done", FenValue.FromBoolean(false), null);
                    iterResult.Set("value", obj.Get(index.ToString(), null), null);
                    index++;
                    return FenValue.FromObject(iterResult);
                })));
                iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iterator))));
                return FenValue.FromObject(iterator);
            })));

            // Array.prototype[Symbol.iterator] - ES2015 (same as values)
            _arrayPrototype.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                int index = 0;
                
                var iterator = new FenObject();
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    if (index >= len)
                    {
                        var doneResult = new FenObject();
                        doneResult.Set("done", FenValue.FromBoolean(true), null);
                        doneResult.Set("value", FenValue.Undefined, null);
                        return FenValue.FromObject(doneResult);
                    }
                    var iterResult = new FenObject();
                    iterResult.Set("done", FenValue.FromBoolean(false), null);
                    iterResult.Set("value", obj.Get(index.ToString(), null), null);
                    index++;
                    return FenValue.FromObject(iterResult);
                })));
                iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iterator))));
                return FenValue.FromObject(iterator);
            })));

            // Array.prototype.toString() - calls join()
            _arrayPrototype.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                var joinFn = _arrayPrototype.Get("join", null);
                if (joinFn.IsFunction)
                    return joinFn.AsFunction().Invoke(new FenValue[0], null);
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.FromString("");
                var lenVal = obj.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++)
                {
                    if (i > 0) sb.Append(",");
                    var v = obj.Get(i.ToString(), null);
                    if (!v.IsNull && !v.IsUndefined) sb.Append(v.ToString());
                }
                return FenValue.FromString(sb.ToString());
            })));

            // Array.prototype.toLocaleString()
            _arrayPrototype.Set("toLocaleString", FenValue.FromFunction(new FenFunction("toLocaleString", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null) return FenValue.FromString("");
                var lenVal = obj.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++)
                {
                    if (i > 0) sb.Append(",");
                    var v = obj.Get(i.ToString(), null);
                    if (!v.IsNull && !v.IsUndefined) sb.Append(v.ToString());
                }
                return FenValue.FromString(sb.ToString());
            })));

            // Array.prototype.group(callbackFn) - ES2024 (returns object)
            _arrayPrototype.Set("group", FenValue.FromFunction(new FenFunction("group", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null || args.Length == 0 || !args[0].IsFunction)
                    return FenValue.FromObject(new FenObject());

                var callback = args[0].AsFunction();
                var lenVal = obj.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;

                var groups = new Dictionary<string, List<FenValue>>();
                for (int i = 0; i < len; i++)
                {
                    var item = obj.Get(i.ToString(), null);
                    var keyResult = callback.Invoke(new FenValue[] { item, FenValue.FromNumber(i), thisVal }, null);
                    var groupKey = keyResult.ToString();

                    if (!groups.ContainsKey(groupKey))
                        groups[groupKey] = new List<FenValue>();
                    groups[groupKey].Add(item);
                }

                var result = new FenObject();
                foreach (var kvp in groups)
                {
                    var groupArr = FenObject.CreateArray();
                    for (int i = 0; i < kvp.Value.Count; i++)
                        groupArr.Set(i.ToString(), kvp.Value[i], null);
                    groupArr.Set("length", FenValue.FromNumber(kvp.Value.Count), null);
                    result.Set(kvp.Key, FenValue.FromObject(groupArr), null);
                }
                return FenValue.FromObject(result);
            })));

            // Array.prototype.groupToMap(callbackFn) - ES2024 (returns Map)
            _arrayPrototype.Set("groupToMap", FenValue.FromFunction(new FenFunction("groupToMap", (args, thisVal) =>
            {
                var obj = thisVal.AsObject();
                if (obj == null || args.Length == 0 || !args[0].IsFunction)
                    return FenValue.FromObject(new FenObject());

                var callback = args[0].AsFunction();
                var lenVal = obj.Get("length", null);
                int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;

                // Create a Map
                var map = new FenObject();
                var storage = new Dictionary<string, (FenValue key, FenValue value)>();
                map.NativeObject = storage;

                Func<FenValue, string> getMapKey = (val) =>
                {
                    if (val.IsUndefined) return "undefined";
                    if (val.IsNull) return "null";
                    if (val.IsBoolean) return "bool:" + val.ToBoolean();
                    if (val.IsNumber) return "num:" + val.ToNumber();
                    if (val.IsString) return "str:" + val.ToString();
                    if (val.IsObject) return "obj:" + val.AsObject().GetHashCode();
                    return "other:" + val.ToString();
                };

                for (int i = 0; i < len; i++)
                {
                    var item = obj.Get(i.ToString(), null);
                    var keyResult = callback.Invoke(new FenValue[] { item, FenValue.FromNumber(i), thisVal }, null);
                    var mapKey = getMapKey(keyResult);

                    if (!storage.ContainsKey(mapKey))
                    {
                        var groupArr = FenObject.CreateArray();
                        groupArr.Set("0", item, null);
                        groupArr.Set("length", FenValue.FromNumber(1), null);
                        storage[mapKey] = (keyResult, FenValue.FromObject(groupArr));
                    }
                    else
                    {
                        var existing = storage[mapKey];
                        var groupArr = existing.value.AsObject() as FenObject;
                        if (groupArr != null)
                        {
                            var currentLen = groupArr.Get("length", null).ToNumber();
                            groupArr.Set(((int)currentLen).ToString(), item, null);
                            groupArr.Set("length", FenValue.FromNumber(currentLen + 1), null);
                        }
                    }
                }

                // Add Map methods
                map.Set("size", FenValue.FromNumber(storage.Count), null);
                map.Set("get", FenValue.FromFunction(new FenFunction("get", (getArgs, _) =>
                {
                    if (getArgs.Length == 0) return FenValue.Undefined;
                    var key = getMapKey(getArgs[0]);
                    return storage.ContainsKey(key) ? storage[key].value : FenValue.Undefined;
                })), null);
                map.Set("has", FenValue.FromFunction(new FenFunction("has", (hasArgs, _) =>
                {
                    if (hasArgs.Length == 0) return FenValue.FromBoolean(false);
                    return FenValue.FromBoolean(storage.ContainsKey(getMapKey(hasArgs[0])));
                })), null);

                return FenValue.FromObject(map);
            })));

            return _arrayPrototype;
        }

        private FenObject CreateArray(string[] items)
        {
            var arr = new FenObject();
            for(int i=0; i<items.Length; i++)
            {
                arr.Set(i.ToString(), FenValue.FromString(items[i]), null);
            }
            arr.Set("length", FenValue.FromNumber(items.Length), null);
            return arr;
        }

        private FenObject CreateEmptyIterator()
        {
            var iter = new FenObject();
            iter.Set("next", FenValue.FromFunction(new FenFunction("next", (a, t) =>
            {
                var res = new FenObject();
                res.Set("value", FenValue.Undefined, null);
                res.Set("done", FenValue.FromBoolean(true), null);
                return FenValue.FromObject(res);
            })), null);
            if (JsSymbol.Iterator != null)
                iter.Set(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iter))), null);
            return iter;
        }

        private FenObject CreateListIterator(System.Collections.Generic.List<FenValue> items)
        {
            var iter = new FenObject();
            int index = 0;
            iter.Set("next", FenValue.FromFunction(new FenFunction("next", (a, t) =>
            {
                var res = new FenObject();
                if (index < items.Count)
                {
                    res.Set("value", items[index++], null);
                    res.Set("done", FenValue.FromBoolean(false), null);
                }
                else
                {
                    res.Set("value", FenValue.Undefined, null);
                    res.Set("done", FenValue.FromBoolean(true), null);
                }
                return FenValue.FromObject(res);
            })), null);
            if (JsSymbol.Iterator != null)
                iter.Set(JsSymbol.Iterator.ToPropertyKey(), FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iter))), null);
            return iter;
        }

        private FenObject GetStringPrototype()
        {
            if (_stringPrototype != null) return _stringPrototype;
            _stringPrototype = new FenObject();

            // String.prototype.includes(searchString, position) - ES2015
            _stringPrototype.Set("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var searchStr = args[0].ToString();
                int position = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (position < 0) position = 0;
                if (position >= str.Length) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(str.IndexOf(searchStr, position, StringComparison.Ordinal) >= 0);
            })));

            // String.prototype.startsWith(searchString, position) - ES2015
            _stringPrototype.Set("startsWith", FenValue.FromFunction(new FenFunction("startsWith", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var searchStr = args[0].ToString();
                int position = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (position < 0) position = 0;
                if (position + searchStr.Length > str.Length) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(str.Substring(position).StartsWith(searchStr, StringComparison.Ordinal));
            })));

            // String.prototype.endsWith(searchString, length) - ES2015
            _stringPrototype.Set("endsWith", FenValue.FromFunction(new FenFunction("endsWith", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var searchStr = args[0].ToString();
                int endPos = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                if (endPos < 0) endPos = 0;
                if (endPos > str.Length) endPos = str.Length;
                if (searchStr.Length > endPos) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(str.Substring(0, endPos).EndsWith(searchStr, StringComparison.Ordinal));
            })));

            // String.prototype.padStart(targetLength, padString) - ES2017
            _stringPrototype.Set("padStart", FenValue.FromFunction(new FenFunction("padStart", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int targetLen = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (targetLen <= str.Length) return FenValue.FromString(str);
                var padStr = args.Length > 1 ? args[1].ToString() : " ";
                if (string.IsNullOrEmpty(padStr)) return FenValue.FromString(str);
                int padLen = targetLen - str.Length;
                var sb = new StringBuilder();
                while (sb.Length < padLen)
                {
                    sb.Append(padStr);
                }
                return FenValue.FromString(sb.ToString().Substring(0, padLen) + str);
            })));

            // String.prototype.padEnd(targetLength, padString) - ES2017
            _stringPrototype.Set("padEnd", FenValue.FromFunction(new FenFunction("padEnd", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int targetLen = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (targetLen <= str.Length) return FenValue.FromString(str);
                var padStr = args.Length > 1 ? args[1].ToString() : " ";
                if (string.IsNullOrEmpty(padStr)) return FenValue.FromString(str);
                int padLen = targetLen - str.Length;
                var sb = new StringBuilder(str);
                while (sb.Length < targetLen)
                {
                    sb.Append(padStr);
                }
                return FenValue.FromString(sb.ToString().Substring(0, targetLen));
            })));

            // String.prototype.trimStart() - ES2019
            _stringPrototype.Set("trimStart", FenValue.FromFunction(new FenFunction("trimStart", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().TrimStart());
            })));

            // String.prototype.trimEnd() - ES2019
            _stringPrototype.Set("trimEnd", FenValue.FromFunction(new FenFunction("trimEnd", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().TrimEnd());
            })));

            // String.prototype.replaceAll(searchValue, replaceValue) - ES2021
            _stringPrototype.Set("replaceAll", FenValue.FromFunction(new FenFunction("replaceAll", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length < 2) return FenValue.FromString(str);
                var searchStr = args[0].ToString();
                var replaceStr = args[1].ToString();
                if (string.IsNullOrEmpty(searchStr)) return FenValue.FromString(str);
                return FenValue.FromString(str.Replace(searchStr, replaceStr));
            })));

            // String.prototype.at(index) - ES2022
            _stringPrototype.Set("at", FenValue.FromFunction(new FenFunction("at", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0) index = str.Length + index;
                if (index < 0 || index >= str.Length) return FenValue.Undefined;
                return FenValue.FromString(str[index].ToString());
            })));

            // String.prototype.isWellFormed() - ES2024
            _stringPrototype.Set("isWellFormed", FenValue.FromFunction(new FenFunction("isWellFormed", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                // Check for lone surrogates (U+D800–U+DFFF not in valid surrogate pair)
                for (int i = 0; i < str.Length; i++)
                {
                    char c = str[i];
                    if (c >= 0xD800 && c <= 0xDBFF) // high surrogate
                    {
                        if (i + 1 >= str.Length || str[i + 1] < 0xDC00 || str[i + 1] > 0xDFFF)
                            return FenValue.FromBoolean(false);
                        i++; // skip low surrogate
                    }
                    else if (c >= 0xDC00 && c <= 0xDFFF) // lone low surrogate
                    {
                        return FenValue.FromBoolean(false);
                    }
                }
                return FenValue.FromBoolean(true);
            })));

            // String.prototype.toWellFormed() - ES2024
            _stringPrototype.Set("toWellFormed", FenValue.FromFunction(new FenFunction("toWellFormed", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                var sb = new StringBuilder(str.Length);
                for (int i = 0; i < str.Length; i++)
                {
                    char c = str[i];
                    if (c >= 0xD800 && c <= 0xDBFF) // high surrogate
                    {
                        if (i + 1 < str.Length && str[i + 1] >= 0xDC00 && str[i + 1] <= 0xDFFF)
                        {
                            sb.Append(c);
                            sb.Append(str[++i]);
                        }
                        else sb.Append('\uFFFD'); // replacement char
                    }
                    else if (c >= 0xDC00 && c <= 0xDFFF) // lone low surrogate
                    {
                        sb.Append('\uFFFD');
                    }
                    else sb.Append(c);
                }
                return FenValue.FromString(sb.ToString());
            })));

            // String.prototype.repeat(count) - ES2015
            _stringPrototype.Set("repeat", FenValue.FromFunction(new FenFunction("repeat", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int count = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (count <= 0) return FenValue.FromString("");
                var sb = new StringBuilder();
                for (int i = 0; i < count; i++) sb.Append(str);
                return FenValue.FromString(sb.ToString());
            })));

            // String.prototype[Symbol.iterator] - ES2015 for...of support
            _stringPrototype.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int index = 0;
                
                var iterator = new FenObject();
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    if (index >= str.Length)
                    {
                        var doneResult = new FenObject();
                        doneResult.Set("done", FenValue.FromBoolean(true), null);
                        doneResult.Set("value", FenValue.Undefined, null);
                        return FenValue.FromObject(doneResult);
                    }
                    var iterResult = new FenObject();
                    iterResult.Set("done", FenValue.FromBoolean(false), null);
                    iterResult.Set("value", FenValue.FromString(str[index].ToString()), null);
                    index++;
                    return FenValue.FromObject(iterResult);
                })));
                iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iterator))));
                return FenValue.FromObject(iterator);
            })));

            // String.prototype.substring(start, end)
            _stringPrototype.Set("substring", FenValue.FromFunction(new FenFunction("substring", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int start = args.Length > 0 ? Math.Max(0, Math.Min((int)args[0].ToNumber(), str.Length)) : 0;
                int end = args.Length > 1 ? Math.Max(0, Math.Min((int)args[1].ToNumber(), str.Length)) : str.Length;
                if (start > end) { int tmp = start; start = end; end = tmp; }
                return FenValue.FromString(str.Substring(start, end - start));
            })));

            // String.prototype.slice(start, end)
            _stringPrototype.Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int end = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                if (start < 0) start = Math.Max(str.Length + start, 0);
                if (end < 0) end = Math.Max(str.Length + end, 0);
                start = Math.Min(start, str.Length);
                end = Math.Min(end, str.Length);
                if (start >= end) return FenValue.FromString("");
                return FenValue.FromString(str.Substring(start, end - start));
            })));

            // String.prototype.split(separator, limit) - ES5.1 with RegExp support
            _stringPrototype.Set("split", FenValue.FromFunction(new FenFunction("split", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                var result = new FenObject();
                if (args.Length == 0 || args[0].IsUndefined)
                {
                    result.Set("0", FenValue.FromString(str), null);
                    result.Set("length", FenValue.FromNumber(1), null);
                    return FenValue.FromObject(result);
                }
                
                int limit = args.Length > 1 && !args[1].IsUndefined ? (int)args[1].ToNumber() : int.MaxValue;
                if (limit == 0) { result.Set("length", FenValue.FromNumber(0), null); return FenValue.FromObject(result); }
                
                string[] parts;
                
                // Check if separator is RegExp
                if (args[0].IsObject)
                {
                    var obj = args[0].AsObject();
                    if (obj is FenObject fenObj && fenObj.NativeObject is System.Text.RegularExpressions.Regex regex)
                    {
                        parts = regex.Split(str);
                    }
                    else
                    {
                        var separator = args[0].ToString();
                        parts = string.IsNullOrEmpty(separator) 
                            ? str.Select(c => c.ToString()).ToArray() 
                            : str.Split(new[] { separator }, StringSplitOptions.None);
                    }
                }
                else
                {
                    var separator = args[0].ToString();
                    parts = string.IsNullOrEmpty(separator) 
                        ? str.Select(c => c.ToString()).ToArray() 
                        : str.Split(new[] { separator }, StringSplitOptions.None);
                }
                
                int count = Math.Min(parts.Length, limit);
                for (int i = 0; i < count; i++)
                {
                    result.Set(i.ToString(), FenValue.FromString(parts[i]), null);
                }
                result.Set("length", FenValue.FromNumber(count), null);
                return FenValue.FromObject(result);
            })));

            // String.prototype.toLowerCase()
            _stringPrototype.Set("toLowerCase", FenValue.FromFunction(new FenFunction("toLowerCase", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().ToLowerInvariant());
            })));

            // String.prototype.toUpperCase()
            _stringPrototype.Set("toUpperCase", FenValue.FromFunction(new FenFunction("toUpperCase", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().ToUpperInvariant());
            })));

            // String.prototype.trim()
            _stringPrototype.Set("trim", FenValue.FromFunction(new FenFunction("trim", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().Trim());
            })));

            // String.prototype.indexOf(searchValue, fromIndex)
            _stringPrototype.Set("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromNumber(-1);
                var search = args[0].ToString();
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (from < 0) from = 0;
                if (from >= str.Length) return FenValue.FromNumber(-1);
                return FenValue.FromNumber(str.IndexOf(search, from, StringComparison.Ordinal));
            })));

            // String.prototype.lastIndexOf(searchValue, fromIndex)
            _stringPrototype.Set("lastIndexOf", FenValue.FromFunction(new FenFunction("lastIndexOf", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromNumber(-1);
                var search = args[0].ToString();
                int from = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                if (from < 0) return FenValue.FromNumber(-1);
                if (from >= str.Length) from = str.Length - 1;
                return FenValue.FromNumber(str.LastIndexOf(search, from, StringComparison.Ordinal));
            })));

            // String.prototype.charAt(index)
            _stringPrototype.Set("charAt", FenValue.FromFunction(new FenFunction("charAt", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0 || index >= str.Length) return FenValue.FromString("");
                return FenValue.FromString(str[index].ToString());
            })));

            // String.prototype.charCodeAt(index)
            _stringPrototype.Set("charCodeAt", FenValue.FromFunction(new FenFunction("charCodeAt", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0 || index >= str.Length) return FenValue.FromNumber(double.NaN);
                return FenValue.FromNumber((int)str[index]);
            })));

            // String.prototype.codePointAt(index) - ES2015
            _stringPrototype.Set("codePointAt", FenValue.FromFunction(new FenFunction("codePointAt", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int index = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (index < 0 || index >= str.Length) return FenValue.Undefined;
                // Check for surrogate pair
                int codePoint = char.ConvertToUtf32(str, index);
                return FenValue.FromNumber(codePoint);
            })));

            // String.prototype.replace(searchValue, replaceValue) - ES5.1 with RegExp and function support
            _stringPrototype.Set("replace", FenValue.FromFunction(new FenFunction("replace", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length < 2) return FenValue.FromString(str);
                
                var searchArg = args[0];
                var replaceArg = args[1];
                
                // Check if search is a RegExp object
                System.Text.RegularExpressions.Regex regex = null;
                bool isGlobal = false;
                
                if (searchArg.IsObject)
                {
                    var obj = searchArg.AsObject();
                    if (obj is FenObject fenObj && fenObj.NativeObject is System.Text.RegularExpressions.Regex r)
                    {
                        regex = r;
                        var flagsVal = fenObj.Get("flags", null);
                        isGlobal = flagsVal.ToString().Contains("g");
                    }
                }
                
                if (regex != null)
                {
                    // RegExp replacement
                    if (replaceArg.IsFunction)
                    {
                        // Function replacer
                        var fn = replaceArg.AsFunction();
                        return FenValue.FromString(regex.Replace(str, m => {
                            var fnArgs = new List<FenValue> { FenValue.FromString(m.Value) };
                            for (int i = 1; i < m.Groups.Count; i++)
                                fnArgs.Add(FenValue.FromString(m.Groups[i].Value));
                            fnArgs.Add(FenValue.FromNumber(m.Index));
                            fnArgs.Add(FenValue.FromString(str));
                            return fn.Invoke(fnArgs.ToArray(), null).ToString();
                        }, isGlobal ? int.MaxValue : 1));
                    }
                    else
                    {
                        // String replacement with $ substitutions
                        var replaceStr = replaceArg.ToString();
                        return FenValue.FromString(regex.Replace(str, replaceStr, isGlobal ? int.MaxValue : 1));
                    }
                }
                else
                {
                    // String search (only first occurrence)
                    var searchStr = searchArg.ToString();
                    int idx = str.IndexOf(searchStr, StringComparison.Ordinal);
                    if (idx < 0) return FenValue.FromString(str);
                    
                    string replaceStr;
                    if (replaceArg.IsFunction)
                    {
                        var fn = replaceArg.AsFunction();
                        var fnResult = fn.Invoke(new[] { FenValue.FromString(searchStr), FenValue.FromNumber(idx), FenValue.FromString(str) }, null);
                        replaceStr = fnResult.ToString();
                    }
                    else
                    {
                        replaceStr = replaceArg.ToString();
                    }
                    return FenValue.FromString(str.Substring(0, idx) + replaceStr + str.Substring(idx + searchStr.Length));
                }
            })));

            // String.prototype.concat(...strings)
            _stringPrototype.Set("concat", FenValue.FromFunction(new FenFunction("concat", (args, thisVal) =>
            {
                var sb = new StringBuilder(thisVal.ToString());
                foreach (var arg in args) sb.Append(arg.ToString());
                return FenValue.FromString(sb.ToString());
            })));

            // String.prototype.match(regexp) - ES5.1
            _stringPrototype.Set("match", FenValue.FromFunction(new FenFunction("match", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.Null;
                
                // Get regex pattern from argument
                System.Text.RegularExpressions.Regex regex = null;
                bool isGlobal = false;
                
                if (args[0].IsObject)
                {
                    var obj = args[0].AsObject();
                    if (obj is FenObject fenObj && fenObj.NativeObject is System.Text.RegularExpressions.Regex r)
                    {
                        regex = r;
                        var flagsVal = fenObj.Get("flags", null);
                        isGlobal = flagsVal.ToString().Contains("g");
                    }
                }
                
                if (regex == null)
                {
                    // Convert to string pattern
                    var pattern = args[0].ToString();
                    try { regex = new System.Text.RegularExpressions.Regex(pattern); }
                    catch { return FenValue.Null; }
                }
                
                if (isGlobal)
                {
                    // Global match - return all matches
                    var matches = regex.Matches(str);
                    if (matches.Count == 0) return FenValue.Null;
                    var result = new FenObject();
                    for (int i = 0; i < matches.Count; i++)
                    {
                        result.Set(i.ToString(), FenValue.FromString(matches[i].Value), null);
                    }
                    result.Set("length", FenValue.FromNumber(matches.Count), null);
                    return FenValue.FromObject(result);
                }
                else
                {
                    // Non-global - return first match with groups
                    var match = regex.Match(str);
                    if (!match.Success) return FenValue.Null;
                    var result = new FenObject();
                    result.Set("0", FenValue.FromString(match.Value), null);
                    for (int i = 1; i < match.Groups.Count; i++)
                    {
                        result.Set(i.ToString(), FenValue.FromString(match.Groups[i].Value), null);
                    }
                    result.Set("length", FenValue.FromNumber(match.Groups.Count), null);
                    result.Set("index", FenValue.FromNumber(match.Index), null);
                    result.Set("input", FenValue.FromString(str), null);
                    return FenValue.FromObject(result);
                }
            })));

            // String.prototype.matchAll(regexp) - ES2020
            _stringPrototype.Set("matchAll", FenValue.FromFunction(new FenFunction("matchAll", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromObject(CreateEmptyIterator());
                System.Text.RegularExpressions.Regex regex = null;
                if (args[0].IsObject)
                {
                    var rgxObj = args[0].AsObject();
                    if (rgxObj is FenObject fenObj && fenObj.NativeObject is System.Text.RegularExpressions.Regex r)
                        regex = r;
                }
                if (regex == null)
                {
                    try { regex = new System.Text.RegularExpressions.Regex(args[0].ToString()); }
                    catch { return FenValue.FromObject(CreateEmptyIterator()); }
                }
                var allMatches = regex.Matches(str);
                var matchList = new System.Collections.Generic.List<FenValue>();
                foreach (System.Text.RegularExpressions.Match m in allMatches)
                {
                    var matchObj = new FenObject();
                    matchObj.Set("0", FenValue.FromString(m.Value), null);
                    for (int gi = 1; gi < m.Groups.Count; gi++)
                        matchObj.Set(gi.ToString(), FenValue.FromString(m.Groups[gi].Value), null);
                    matchObj.Set("length", FenValue.FromNumber(m.Groups.Count), null);
                    matchObj.Set("index", FenValue.FromNumber(m.Index), null);
                    matchObj.Set("input", FenValue.FromString(str), null);
                    matchList.Add(FenValue.FromObject(matchObj));
                }
                return FenValue.FromObject(CreateListIterator(matchList));
            })));

            // String.prototype.search(regexp) - ES5.1
            _stringPrototype.Set("search", FenValue.FromFunction(new FenFunction("search", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromNumber(-1);
                
                System.Text.RegularExpressions.Regex regex = null;
                
                if (args[0].IsObject)
                {
                    var obj = args[0].AsObject();
                    if (obj is FenObject fenObj && fenObj.NativeObject is System.Text.RegularExpressions.Regex r)
                    {
                        regex = r;
                    }
                }
                
                if (regex == null)
                {
                    var pattern = args[0].ToString();
                    try { regex = new System.Text.RegularExpressions.Regex(pattern); }
                    catch { return FenValue.FromNumber(-1); }
                }
                
                var match = regex.Match(str);
                return FenValue.FromNumber(match.Success ? match.Index : -1);
            })));

            // String.prototype.codePointAt(pos) - ES2015
            _stringPrototype.Set("codePointAt", FenValue.FromFunction(new FenFunction("codePointAt", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int pos = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (pos < 0 || pos >= str.Length) return FenValue.Undefined;
                int cp = char.ConvertToUtf32(str, pos);
                return FenValue.FromNumber(cp);
            })));

            // String.prototype.normalize([form]) - ES2015
            _stringPrototype.Set("normalize", FenValue.FromFunction(new FenFunction("normalize", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                var form = args.Length > 0 ? args[0].ToString() : "NFC";
                System.Text.NormalizationForm nf;
                switch (form)
                {
                    case "NFD": nf = System.Text.NormalizationForm.FormD; break;
                    case "NFKC": nf = System.Text.NormalizationForm.FormKC; break;
                    case "NFKD": nf = System.Text.NormalizationForm.FormKD; break;
                    default: nf = System.Text.NormalizationForm.FormC; break;
                }
                return FenValue.FromString(str.Normalize(nf));
            })));

            // String.prototype.localeCompare(other) - basic implementation
            _stringPrototype.Set("localeCompare", FenValue.FromFunction(new FenFunction("localeCompare", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                var other = args.Length > 0 ? args[0].ToString() : "";
                return FenValue.FromNumber(string.Compare(str, other, System.StringComparison.CurrentCulture));
            })));

            // String.prototype.toString() - returns primitive string value
            _stringPrototype.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
                FenValue.FromString(thisVal.ToString()))));

            // String.prototype.valueOf() - returns primitive string value
            _stringPrototype.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
                FenValue.FromString(thisVal.ToString()))));

            // String.prototype.match(regexp) - ES3/ES2015
            _stringPrototype.Set("match", FenValue.FromFunction(new FenFunction("match", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.Null;

                // Get the regex or convert string to regex
                FenValue regexVal = args[0];
                if (!regexVal.IsObject)
                {
                    // Convert string to regex
                    var pattern = regexVal.ToString();
                    var regexObj = new FenObject();
                    regexObj.InternalClass = "RegExp";
                    try
                    {
                        var re = new System.Text.RegularExpressions.Regex(pattern);
                        regexObj.NativeObject = re;
                        regexObj.Set("source", FenValue.FromString(pattern), null);
                        regexObj.Set("flags", FenValue.FromString(""), null);
                        regexVal = FenValue.FromObject(regexObj);
                    }
                    catch { return FenValue.Null; }
                }

                var obj = regexVal.AsObject();
                if (obj == null) return FenValue.Null;

                // Call the regex's exec method or use native Regex
                var execMethod = obj.Get("exec", null);
                if (execMethod.IsFunction)
                    return execMethod.AsFunction().Invoke(new FenValue[] { FenValue.FromString(str) }, null);

                // Fallback: use native .NET Regex
                var fenObj = obj as FenObject;
                if (fenObj?.NativeObject is System.Text.RegularExpressions.Regex regex)
                {
                    var match = regex.Match(str);
                    if (!match.Success) return FenValue.Null;

                    var result = FenObject.CreateArray();
                    result.Set("0", FenValue.FromString(match.Value), null);
                    for (int i = 1; i < match.Groups.Count; i++)
                    {
                        result.Set(i.ToString(), match.Groups[i].Success
                            ? FenValue.FromString(match.Groups[i].Value)
                            : FenValue.Undefined, null);
                    }
                    result.Set("length", FenValue.FromNumber(match.Groups.Count), null);
                    result.Set("index", FenValue.FromNumber(match.Index), null);
                    result.Set("input", FenValue.FromString(str), null);
                    return FenValue.FromObject(result);
                }

                return FenValue.Null;
            })));

            // String.prototype.search(regexp) - ES3
            _stringPrototype.Set("search", FenValue.FromFunction(new FenFunction("search", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromNumber(-1);

                // Get the regex or convert string to regex
                FenValue regexVal = args[0];
                if (!regexVal.IsObject)
                {
                    var pattern = regexVal.ToString();
                    try
                    {
                        var regex = new System.Text.RegularExpressions.Regex(pattern);
                        var match = regex.Match(str);
                        return FenValue.FromNumber(match.Success ? match.Index : -1);
                    }
                    catch { return FenValue.FromNumber(-1); }
                }

                var obj = regexVal.AsObject();
                if (obj == null) return FenValue.FromNumber(-1);

                // Use native .NET Regex
                var fenObj = obj as FenObject;
                if (fenObj?.NativeObject is System.Text.RegularExpressions.Regex regex2)
                {
                    var match = regex2.Match(str);
                    return FenValue.FromNumber(match.Success ? match.Index : -1);
                }

                return FenValue.FromNumber(-1);
            })));

            // String.prototype.matchAll(regexp) - ES2020
            _stringPrototype.Set("matchAll", FenValue.FromFunction(new FenFunction("matchAll", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.Null;

                // Get the regex
                FenValue regexVal = args[0];
                if (!regexVal.IsObject)
                {
                    var pattern = regexVal.ToString();
                    var regexObj = new FenObject();
                    regexObj.InternalClass = "RegExp";
                    try
                    {
                        // matchAll requires global flag
                        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None);
                        regexObj.NativeObject = regex;
                        regexVal = FenValue.FromObject(regexObj);
                    }
                    catch { return FenValue.Null; }
                }

                var obj = regexVal.AsObject();
                var fenObj = obj as FenObject;
                if (fenObj?.NativeObject is not System.Text.RegularExpressions.Regex regex3)
                    return FenValue.Null;

                // Create iterator that yields all matches
                var matches = regex3.Matches(str);
                int matchIndex = 0;
                var iterator = new FenObject();
                iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (_, __) =>
                {
                    var iterResult = new FenObject();
                    if (matchIndex >= matches.Count)
                    {
                        iterResult.Set("done", FenValue.FromBoolean(true), null);
                        iterResult.Set("value", FenValue.Undefined, null);
                        return FenValue.FromObject(iterResult);
                    }

                    var match = matches[matchIndex++];
                    var matchArr = FenObject.CreateArray();
                    matchArr.Set("0", FenValue.FromString(match.Value), null);
                    for (int i = 1; i < match.Groups.Count; i++)
                    {
                        matchArr.Set(i.ToString(), match.Groups[i].Success
                            ? FenValue.FromString(match.Groups[i].Value)
                            : FenValue.Undefined, null);
                    }
                    matchArr.Set("length", FenValue.FromNumber(match.Groups.Count), null);
                    matchArr.Set("index", FenValue.FromNumber(match.Index), null);
                    matchArr.Set("input", FenValue.FromString(str), null);

                    iterResult.Set("done", FenValue.FromBoolean(false), null);
                    iterResult.Set("value", FenValue.FromObject(matchArr), null);
                    return FenValue.FromObject(iterResult);
                })), null);
                iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iterator))));
                return FenValue.FromObject(iterator);
            })));

            return _stringPrototype;
        }

        private FenObject FlattenArray(IObject arr, int depth)
        {
            var result = new FenObject();
            int idx = 0;
            
            void FlattenRecursive(IObject source, int currentDepth)
            {
                var arrLen = source.Get("length", null);
                int l = arrLen != null && arrLen.IsNumber ? (int)arrLen.ToNumber() : 0;
                for (int i = 0; i < l; i++)
                {
                    var val = source.Get(i.ToString(), null);
                    if (currentDepth > 0 && val != null && val.IsObject)
                    {
                        var inner = val.AsObject();
                        var innerLen = inner?.Get("length", null);
                        if (innerLen != null && innerLen.Value.IsNumber)
                        {
                            FlattenRecursive(inner, currentDepth - 1);
                            continue;
                        }
                    }
                    result.Set(idx.ToString(), val , null);
                    idx++;
                }
            }
            
            FlattenRecursive(arr, depth);
            result.Set("length", FenValue.FromNumber(idx), null);
            return result;
        }

        private IValue EvalObjectLiteral(ObjectLiteral node, FenEnvironment env, IExecutionContext context)
        {
            var obj = new FenObject();
            foreach (var pair in node.Pairs)
            {
                // Handle spread: { ...source }
                if (pair.Key.StartsWith("__spread_") && pair.Value is SpreadElement spread)
                {
                    var spreadVal = Eval(spread.Argument, env, context);
                    if (IsError(spreadVal)) return spreadVal;
                    if (spreadVal.IsObject)
                    {
                        var srcObj = spreadVal.AsObject();
                        if (srcObj != null)
                        {
                            foreach (var k in srcObj.Keys())
                                obj.Set(k, srcObj.Get(k), null);
                        }
                    }
                    continue;
                }

                // Evaluate the value
                var val = Eval(pair.Value, env, context);
                if (IsError(val)) return val;

                // Handle computed property keys: { [expr]: value }
                if (pair.Key.StartsWith("__computed_") && node.ComputedKeys.TryGetValue(pair.Key, out var computedKeyExpr))
                {
                    var keyVal = Eval(computedKeyExpr, env, context);
                    if (IsError(keyVal)) return keyVal;
                    obj.Set(keyVal.AsString(), val, null);
                    continue;
                }

                // Handle getter/setter: { get foo() {}, set foo(v) {} }
                if (pair.Key.StartsWith("__get_") || pair.Key.StartsWith("__set_"))
                {
                    // For now, store as regular properties — full getter/setter support would
                    // need property descriptors on FenObject. Store the function value.
                    string realKey = pair.Key.Substring(6); // remove __get_ or __set_
                    obj.Set(pair.Key, val, null); // keep prefixed for accessor dispatch
                    continue;
                }

                obj.Set(pair.Key, val, null);
            }
            return FenValue.FromObject(obj);
        }

        private IValue EvalProgram(Program program, FenEnvironment env, IExecutionContext context)
        {
            FenValue result = FenValue.Null;

            // Detect "use strict" directive at the start of the program
            bool previousStrictMode = context.StrictMode;
            if (HasUseStrictDirective(program.Statements))
            {
                context.StrictMode = true;
            }

            // Hoist functions!
            HoistTopLevelFunctions(program, env, context.StrictMode);

            foreach (var stmt in program.Statements)
            {
                result = Eval(stmt, env, context);

                if (result.Type == JsValueType.ReturnValue)
                {
                    context.StrictMode = previousStrictMode; // Restore
                    return result.GetReturnValue();
                }

                if (result.Type == JsValueType.Error)
                {
                    context.StrictMode = previousStrictMode; // Restore
                    return result;
                }
            }

            context.StrictMode = previousStrictMode; // Restore
            return result;
        }

        // Helper to hoist function declarations at top-level
        private void HoistTopLevelFunctions(Program program, FenEnvironment env, bool strictMode)
        {
            foreach (var stmt in program.Statements)
            {
                if (stmt is FunctionDeclarationStatement funcDecl)
                {
                    // Create function upfront (hoisting)
                    if (funcDecl.Function.Name == null) continue;
                    
                    var funcObj = new FenFunction(funcDecl.Function.Parameters, funcDecl.Function.Body, env);
                    funcObj.IsGenerator = funcDecl.Function.IsGenerator;
                    funcObj.IsAsync = funcDecl.Function.IsAsync;
                    if (!string.IsNullOrEmpty(funcDecl.Function.Source))
                        funcObj.Source = funcDecl.Function.Source;

                    var fnVal = FenValue.FromFunction(funcObj);
                    env.Set(funcDecl.Function.Name, fnVal);
                }
            }
        }

        /// <summary>
        /// Check if statements start with a "use strict" directive
        /// </summary>
        private bool HasUseStrictDirective(List<Statement> statements)
        {
            if (statements == null || statements.Count == 0) return false;
            
            // Directive prologue: one or more leading string-literal expression statements.
            // Strict mode is enabled if any directive literal is exactly "use strict".
            foreach (var stmt in statements)
            {
                if (stmt is not ExpressionStatement exprStmt || exprStmt.Expression is not StringLiteral strLit)
                {
                    break;
                }

                var raw = strLit.Value ?? string.Empty;
                var normalized = raw.Trim();
                if (normalized.Length >= 2 &&
                    ((normalized.StartsWith("\"") && normalized.EndsWith("\"")) ||
                     (normalized.StartsWith("'") && normalized.EndsWith("'"))))
                {
                    normalized = normalized.Substring(1, normalized.Length - 2);
                }

                if (normalized == "use strict")
                {
                    return true;
                }
            }

            return false;
        }

        private IValue EvalBlockStatement(BlockStatement block, FenEnvironment env, IExecutionContext context)
        {
            FenValue result = FenValue.Null;
            
            // TDZ: First pass - scan for all let/const declarations and add them to TDZ
            // This ensures accessing them before initialization throws an error
            foreach (var stmt in block.Statements)
            {
                if (stmt is LetStatement letStmt && letStmt.Name != null)
                {
                    env.DeclareTDZ(letStmt.Name.Value);
                }
            }

            // Hoist Function Declarations in Block
            foreach (var stmt in block.Statements)
            {
                if (stmt is FunctionDeclarationStatement funcDecl && funcDecl.Function.Name != null)
                {
                    // For now, just pre-declare / init to Undefined if needed?
                    // Or actually create the function?
                    // Standard hoisting creates the binding.
                    // We'll let Eval() handle the actual value assignment mostly, but strict mode block functions needed hoisting.
                    
                    // In Strict Mode, block-level functions are hoisted to block top.
                    // In Sloppy Mode, they are hoisted to function scope (Annex B).
                    
                    if (context.StrictMode)
                    {
                         // Hoist to block scope (init with undefined or early create?)
                         // Usually functions are initialized at top of block.
                         var funcObj = new FenFunction(funcDecl.Function.Parameters, funcDecl.Function.Body, env);
                         funcObj.IsGenerator = funcDecl.Function.IsGenerator;
                         funcObj.IsAsync = funcDecl.Function.IsAsync;
                         if (!string.IsNullOrEmpty(funcDecl.Function.Source)) funcObj.Source = funcDecl.Function.Source;
                         
                         env.Set(funcDecl.Function.Name, FenValue.FromFunction(funcObj));
                    }
                    else
                    {
                         // Annex B: Hoist to FUNCTION scope (Outer).
                         // For now, let's keep it simple: hoist to current block to ensure visibility anywhere in block.
                         // Truly implementing Annex B require finding the nearest Function env.
                         
                         var funcObj = new FenFunction(funcDecl.Function.Parameters, funcDecl.Function.Body, env);
                         funcObj.IsGenerator = funcDecl.Function.IsGenerator;
                         funcObj.IsAsync = funcDecl.Function.IsAsync;
                         if (!string.IsNullOrEmpty(funcDecl.Function.Source)) funcObj.Source = funcDecl.Function.Source;
                         env.Set(funcDecl.Function.Name, FenValue.FromFunction(funcObj));
                    }
                }
            }

            // Second pass - evaluate statements (let/const will be initialized and removed from TDZ)
            foreach (var stmt in block.Statements)
            {
                result = Eval(stmt, env, context);

                if (result != null && (
                    result.Type == JsValueType.ReturnValue || 
                    result.Type == JsValueType.Error ||
                    result.Type == JsValueType.Break ||
                    result.Type == JsValueType.Continue))
                {
                    return result;
                }
            }

            return result;
        }

        private FenValue EvalPrefixExpression(string op, FenValue right)
        {
            switch (op)
            {
                case "!":
                    return EvalBangOperatorExpression(right);
                case "-":
                    return EvalMinusPrefixOperatorExpression(right);
                case "+":
                    // ES5.1: Unary + converts to number
                    return FenValue.FromNumber(right.ToNumber());
                case "~":
                    // ES5.1: Bitwise NOT - convert to 32-bit int, then NOT
                    var num = (int)right.ToNumber();
                    return FenValue.FromNumber(~num);
                case "typeof":
                    return EvalTypeofExpression(right);
                case "void":
                    return FenValue.Undefined;
                case "delete":
                    return FenValue.FromBoolean(true);  // Actual delete handled in Eval switch
                case "++":
                case "--":
                    return FenValue.FromError($"operator {op} must be handled by EvalPrefixUpdate");
                default:
                    return FenValue.FromError($"unknown operator: {op}{right.Type}");
            }
        }

        private FenValue EvalTypeofExpression(FenValue val)
        {
            switch (val.Type)
            {
                case JsValueType.Undefined:
                    return FenValue.FromString("undefined");
                case JsValueType.Null:
                    return FenValue.FromString("object");  // typeof null === "object" in JS
                case JsValueType.Boolean:
                    return FenValue.FromString("boolean");
                case JsValueType.Number:
                    return FenValue.FromString("number");
                case JsValueType.String:
                    return FenValue.FromString("string");
                case JsValueType.Function:
                    return FenValue.FromString("function");
                case JsValueType.Symbol:
                    return FenValue.FromString("symbol");
                case JsValueType.BigInt:
                    return FenValue.FromString("bigint");
                default:
                    return FenValue.FromString("object");
            }
        }

        // ES5.1 3A: delete operator - properly remove properties from objects
        private FenValue EvalDeleteExpression(AstNode operand, FenEnvironment env, IExecutionContext context)
        {
            // delete obj.prop
            if (operand is MemberExpression memberExpr)
            {
                var obj = Eval(memberExpr.Object, env, context);
                if (obj.Type == JsValueType.Error) return obj;
                
                if (obj.IsObject)
                {
                    var fenObj = obj.AsObject();
                    if (fenObj != null)
                    {
                        return FenValue.FromBoolean(fenObj.Delete(memberExpr.Property));
                    }
                }
                return FenValue.FromBoolean(true); // Deleting non-existent property returns true
            }
            
            // delete obj[key]
            if (operand is IndexExpression indexExpr)
            {
                var obj = Eval(indexExpr.Left, env, context);
                if (obj.Type == JsValueType.Error) return obj;
                
                var key = Eval(indexExpr.Index, env, context);
                if (key.Type == JsValueType.Error) return key;
                
                if (obj.IsObject)
                {
                    var fenObj = obj.AsObject();
                    if (fenObj != null)
                    {
                        return FenValue.FromBoolean(fenObj.Delete(key.ToString()));
                    }
                }
                return FenValue.FromBoolean(true);
            }
            
            // delete identifier - in non-strict mode returns false for declared vars
            if (operand is Identifier ident)
            {
                // In strict mode, deleting an identifier is a SyntaxError
                if (context.StrictMode)
                {
                    return FenValue.FromError($"TypeError: Delete of an unqualified identifier in strict mode.");
                }
                // Variables declared with var/let/const cannot be deleted
                return FenValue.FromBoolean(false);
            }
            
            // Other forms of delete return true
            return FenValue.FromBoolean(true);
        }

        private FenValue EvalBangOperatorExpression(FenValue right)
        {
            return FenValue.FromBoolean(!right.ToBoolean());
        }

        private FenValue EvalMinusPrefixOperatorExpression(FenValue right)
        {
            if (right.Type != JsValueType.Number)
            {
                return FenValue.FromError($"unknown operator: -{right.Type}");
            }
            return FenValue.FromNumber(-right.AsNumber());
        }

        private FenValue EvalInfixExpression(string op, FenValue left, FenValue right)
        {
            // Increment/decrement are handled by Eval in the main switch


            if (op == "&&")
            {
                return left.ToBoolean() ? right : left;
            }
            if (op == "||")
            {
                return left.ToBoolean() ? left : right;
            }
            if (op == ",")
            {
                return right;
            }

            // Strict equality
            if (op == "===")
            {
                return FenValue.FromBoolean(left.StrictEquals(right));
            }
            if (op == "!==")
            {
                return FenValue.FromBoolean(!left.StrictEquals(right));
            }

            // instanceof - check prototype chain
            if (op == "instanceof")
            {
                if (!right.IsFunction) return FenValue.FromBoolean(false);
                var constructor = right.AsFunction();
                if (constructor  == null) return FenValue.FromBoolean(false);
                
                // Get the prototype property of the constructor
                var prototype = constructor.Prototype;
                if (prototype  == null) return FenValue.FromBoolean(false);
                
                // Check if left is an object and walk its prototype chain
                if (!left.IsObject) return FenValue.FromBoolean(false);
                var obj = left.AsObject() as FenObject;
                if (obj  == null) return FenValue.FromBoolean(false);
                
                // Walk the prototype chain
                var currentProto = obj.GetPrototype();
                while (currentProto != null)
                {
                    if (currentProto == prototype) return FenValue.FromBoolean(true);
                    currentProto = (currentProto as FenObject)?.GetPrototype();
                }
                return FenValue.FromBoolean(false);
            }

            // in operator - ES5.1: uses [[HasProperty]] which checks prototype chain
            if (op == "in")
            {
                if (right.IsObject)
                {
                    var obj = right.AsObject();
                    if (obj != null)
                    {
                        // Use Has() which properly checks prototype chain
                        return FenValue.FromBoolean(obj.Has(left.ToString(), null));
                    }
                }
                return FenValue.FromBoolean(false);
            }

            // ES5.1 Addition operator - apply ToPrimitive for objects first
            if (op == "+")
            {
                // Apply ToPrimitive to objects (ES5.1 §11.6.1)
                var lprim = left.IsObject ? left.ToPrimitive() : left;
                var rprim = right.IsObject ? right.ToPrimitive() : right;
                
                // If either is a string, concatenate
                if (lprim.Type == JsValueType.String || rprim.Type == JsValueType.String)
                {
                    return FenValue.FromString(lprim.ToString() + rprim.ToString());
                }
                // Otherwise, add as numbers
                return FenValue.FromNumber(lprim.ToNumber() + rprim.ToNumber());
            }

            if (left.Type == JsValueType.Number && right.Type == JsValueType.Number)
            {
                return EvalIntegerInfixExpression(op, left, right);
            }
            if (left.Type == JsValueType.String && right.Type == JsValueType.String)
            {
                // Handle string comparison operators
                switch (op)
                {
                    case "+":
                        return FenValue.FromString(left.ToString() + right.ToString());
                    case "==":
                    case "===":
                        return FenValue.FromBoolean(left.ToString() == right.ToString());
                    case "!=":
                    case "!==":
                        return FenValue.FromBoolean(left.ToString() != right.ToString());
                    case "<":
                        return FenValue.FromBoolean(string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal) < 0);
                    case ">":
                        return FenValue.FromBoolean(string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal) > 0);
                    case "<=":
                        return FenValue.FromBoolean(string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal) <= 0);
                    case ">=":
                        return FenValue.FromBoolean(string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal) >= 0);
                    default:
                        return FenValue.FromError($"unknown operator: {left.Type} {op} {right.Type}");
                }
            }
            if (op == "==")
                return FenValue.FromBoolean(left.LooseEquals(right));
            if (op == "!=")
                return FenValue.FromBoolean(!left.LooseEquals(right));

            // ES5.1 §11.8: Abstract Relational Comparison for mixed types
            if (op == "<")
            {
                var r = FenValue.AbstractRelationalComparison(left, right);
                return r.IsUndefined ? FenValue.FromBoolean(false) : FenValue.FromBoolean(r.ToBoolean());
            }
            if (op == ">")
            {
                var r = FenValue.AbstractRelationalComparison(right, left, false);
                return r.IsUndefined ? FenValue.FromBoolean(false) : FenValue.FromBoolean(r.ToBoolean());
            }
            if (op == "<=")
            {
                var r = FenValue.AbstractRelationalComparison(right, left, false);
                return r.IsUndefined ? FenValue.FromBoolean(false) : FenValue.FromBoolean(!r.ToBoolean());
            }
            if (op == ">=")
            {
                var r = FenValue.AbstractRelationalComparison(left, right);
                return r.IsUndefined ? FenValue.FromBoolean(false) : FenValue.FromBoolean(!r.ToBoolean());
            }

            if (left.Type != right.Type)
                return FenValue.FromError($"type mismatch: {left.Type} {op} {right.Type}");
            return FenValue.FromError($"unknown operator: {left.Type} {op} {right.Type}");
        }

        private FenValue EvalIntegerInfixExpression(string op, FenValue left, FenValue right)
        {
            var leftVal = left.AsNumber();
            var rightVal = right.AsNumber();

            switch (op)
            {
                case "+":
                    return FenValue.FromNumber(leftVal + rightVal);
                case "-":
                    return FenValue.FromNumber(leftVal - rightVal);
                case "*":
                    return FenValue.FromNumber(leftVal * rightVal);
                case "/":
                    return FenValue.FromNumber(leftVal / rightVal);
                case "%":
                    return FenValue.FromNumber(leftVal % rightVal);
                case "<":
                    return FenValue.FromBoolean(leftVal < rightVal);
                case ">":
                    return FenValue.FromBoolean(leftVal > rightVal);
                case "<=":
                    return FenValue.FromBoolean(leftVal <= rightVal);
                case ">=":
                    return FenValue.FromBoolean(leftVal >= rightVal);
                case "==":
                    return FenValue.FromBoolean(leftVal == rightVal);
                case "!=":
                    return FenValue.FromBoolean(leftVal != rightVal);
                case "===":
                    return FenValue.FromBoolean(leftVal == rightVal);
                case "!==":
                    return FenValue.FromBoolean(leftVal != rightVal);
                default:
                    return FenValue.FromError($"unknown operator: {left.Type} {op} {right.Type}");
            }
        }

        private FenValue EvalStringInfixExpression(string op, FenValue left, FenValue right)
        {
            if (op != "+")
            {
                return FenValue.FromError($"unknown operator: {left.Type} {op} {right.Type}");
            }
            return FenValue.FromString(left.AsString() + right.AsString());
        }

        private IValue EvalIfExpression(IfExpression ie, FenEnvironment env, IExecutionContext context)
        {
            var condition = Eval(ie.Condition, env, context);
            if (IsError(condition)) return condition;

            if (IsTruthy(condition))
            {
                return Eval(ie.Consequence, env, context);
            }
            else if (ie.Alternative != null)
            {
                return Eval(ie.Alternative, env, context);
            }
            else
            {
                return FenValue.Null;
            }
        }

        private IValue EvalIdentifier(Identifier node, FenEnvironment env)
        {
            var val = env.Get(node.Value);
            if (val  == null)
            {
                return FenValue.FromError($"identifier not found: {node.Value}");
            }
            return val;
        }

        private IValue EvalPrivateIdentifier(PrivateIdentifier node, FenEnvironment env)
        {
            // Private identifier access: this.#field
            // We need to access from 'this' in the environment
            var thisVal = env.Get("this");
            if (thisVal  == null || !thisVal.IsObject)
            {
                return FenValue.FromError($"Cannot use private field '#${node.Name}' outside of a class");
            }
            
            var thisObj = thisVal.AsObject();
            if (thisObj  == null)
            {
                return FenValue.FromError($"Cannot use private field '#${node.Name}' outside of a class");
            }
            
            // Private fields are stored with a # prefix
            string privateKey = "#" + node.Name;
            if (thisObj.Has(privateKey))
            {
                return thisObj.Get(privateKey);
            }
            
            return FenValue.FromError($"Cannot read private member #{node.Name} from an object whose class did not declare it");
        }

        private List<FenValue> EvalExpressions(List<Expression> exps, FenEnvironment env, IExecutionContext context)
        {
            var result = new List<FenValue>();

            foreach (var e in exps)
            {
                var evaluated = Eval(e, env, context);
                if (IsError(evaluated))
                {
                    return new List<FenValue> { evaluated };
                }
                result.Add(evaluated);
            }

            return result;
        }

        private FenValue ToFenValue(IValue val)
        {
            if (val == null) return FenValue.Undefined;
            if (val is IObject obj) return FenValue.FromObject(obj);
            // FenValue is a struct implementing IValue, so it will be boxed
            // Try to cast as object first, then unbox
            var boxed = val as object;
            if (boxed != null && boxed.GetType() == typeof(FenValue))
                return (FenValue)boxed;
            // Handle other IValue implementations
            return FenValue.Undefined;
        }

        public FenBrowser.FenEngine.Core.FenValue ApplyFunction(FenBrowser.FenEngine.Core.FenValue fn, List<FenBrowser.FenEngine.Core.FenValue> args, IExecutionContext context, FenBrowser.FenEngine.Core.FenValue thisContext = default)
        {
            if (!_jitInitialized)
            {
                JitRuntime.Initialize(this);
                _jitInitialized = true;
            }
            var function = fn.AsFunction();
            if (function != null)
            {
                try { FenLogger.Debug($"[Interpreter] ApplyFunction: {function.Name ?? "anonymous"}", LogCategory.JavaScript); } catch { }

                // Handle native functions

                if (function.IsNative)
                {
                    var prevThis = context.ThisBinding;
                    context.ThisBinding = thisContext ;
                    try
                    {
                        return function.Invoke(args.ToArray(), context);
                    }
                    finally
                    {
                        context.ThisBinding = prevThis;
                    }
                }
                
                // JIT INTEGRATION
                function.CallCount++;
                if (!function.IsJitCompiled && function.CallCount >= 50 && !function.IsGenerator && !function.IsAsync)
                {
                    try
                    {
                        var compiler = new BytecodeCompiler();
                        var unit = function.Body is BlockStatement bs ? 
                                    compiler.CompileFunction(new FunctionLiteral { Body = bs, Parameters = function.Parameters }) : 
                                    null; // Need better handling for non-block bodies
                        
                        if (unit != null)
                        {
                            // DEBUG: Log bytecode
                            FenLogger.Info($"[JIT] Bytecode for {function.Name}:", LogCategory.JavaScript);
                            for (int i = 0; i < unit.Instructions.Count; i++)
                            {
                                FenLogger.Info($"  {i}: {unit.Instructions[i].OpCode} {unit.Instructions[i].Operand}", LogCategory.JavaScript);
                            }

                            var jit = new JitCompiler();
                            function.JittedDelegate = jit.Compile(unit);
                            function.LocalMap = unit.LocalMap;
                            function.IsJitCompiled = true;
                            FenLogger.Info($"[JIT] Compiled function: {function.Name}", LogCategory.JavaScript);
                        }
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Error($"[JIT] Failed to compile function {function.Name}: {ex.Message}", LogCategory.JavaScript);
                        function.CallCount = -1000; // Stop trying to JIT for a while
                    }
                }

                if (function.IsJitCompiled && function.JittedDelegate != null)
                {
                    context.PushCallFrame(function.Name ?? "anonymous (JIT)");
                    var jittedExtendedEnv = ExtendFunctionEnv(function, args, context, thisContext);
                    try
                    {
                        return function.JittedDelegate(args.ToArray(), jittedExtendedEnv, context);
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Error($"[JIT] Runtime error in {function.Name}: {ex.Message}\n{ex.StackTrace}", LogCategory.JavaScript);
                        // Fallback to interpreter
                        function.IsJitCompiled = false;
                        function.JittedDelegate = null;
                        function.CallCount = -10000;
                    }
                    finally
                    {
                        context.PopCallFrame();
                    }
                }
                
                // Handle generator functions — return a generator object instead of executing
                if (function.IsGenerator)
                {
                    return CreateGeneratorObject(function, args, context, thisContext);
                }

                // Handle user-defined functions (Interpreter fallback)
                context.PushCallFrame(function.Name ?? "anonymous");
                var extendedEnv = ExtendFunctionEnv(function, args, context, thisContext);

                // DevTools CallStack Hook
                DevToolsCore.Instance.PushCallFrame(function.Name ?? "anonymous", context.CurrentUrl, -1, extendedEnv);

                // Detect "use strict" directive in function body
                bool previousStrictMode = context.StrictMode;
                if (function.Body is BlockStatement blockStmt && HasUseStrictDirective(blockStmt.Statements))
                {
                    context.StrictMode = true;
                }

                IValue evaluated;
                try
                {
                    evaluated = Eval(function.Body, extendedEnv, context);
                }
                finally
                {
                    context.StrictMode = previousStrictMode; // Restore
                    DevToolsCore.Instance.PopCallFrame();
                    context.PopCallFrame();
                }

                var result = UnwrapReturnValue(ToFenValue(evaluated));

                // Async functions always return a Promise wrapping the result
                if (function.IsAsync)
                {
                    // If already a promise, return as-is
                    if (result.IsObject && result.AsObject() is Types.JsPromise)
                        return result;
                    // If it's an error, return a rejected promise
                    if (result.Type == Core.Interfaces.ValueType.Error)
                        return FenValue.FromObject(Types.JsPromise.Reject(result, context));
                    return FenValue.FromObject(Types.JsPromise.Resolve(result, context));
                }
                return result;
            }
            
            return FenValue.FromError($"not a function: {fn.Type}");
        }

        /// <summary>
        /// Creates a generator object with next(), return(), throw() methods.
        /// Uses a statement-index coroutine: the body is executed statement-by-statement,
        /// pausing at each yield and resuming on next().
        /// </summary>
        private FenValue CreateGeneratorObject(FenFunction function, List<FenValue> args, IExecutionContext context, FenValue thisContext)
        {
            var genEnv = ExtendFunctionEnv(function, args, context, thisContext);
            var body = function.Body as BlockStatement;
            var statements = body?.Statements ?? new List<Statement>();
            int stmtIndex = 0;
            bool done = false;
            FenObject delegatedIterator = null; // For yield* delegation
            FenFunction delegatedNextFn = null; // For yield* delegation

            // Helper: execute statements from stmtIndex until yield or end
            FenValue RunUntilYield()
            {
                while (stmtIndex < statements.Count)
                {
                    var result = Eval(statements[stmtIndex], genEnv, context);
                    stmtIndex++;

                    if (result.Type == JsValueType.Yield)
                    {
                        // Yield value — pause execution, return {value, done: false}
                        var iterResult = new FenObject();
                        iterResult.Set("value", result.InnerValue);
                        iterResult.Set("done", FenValue.FromBoolean(false));
                        return FenValue.FromObject(iterResult);
                    }

                    // ES2015 yield* delegation: iterate inner generator/iterable
                    if (result.Type == JsValueType.YieldDelegate)
                    {
                        // Get the inner iterable and set up delegation
                        var innerIterable = result.InnerValue;
                        if (innerIterable.IsObject)
                        {
                            var innerObj = innerIterable.AsObject() as FenObject;
                            if (innerObj != null)
                            {
                                // Get the iterator from the inner iterable
                                var innerIterator = innerObj;
                                
                                // Check if it has Symbol.iterator - if so, get the iterator
                                var symbolIterator = innerObj.Get("[Symbol.iterator]", context);
                                if (symbolIterator.IsFunction)
                                {
                                    var iterFn = symbolIterator.AsFunction();
                                    var iterResultVal = iterFn.Invoke(Array.Empty<FenValue>(), context);
                                    if (iterResultVal.IsObject)
                                    {
                                        innerIterator = iterResultVal.AsObject() as FenObject;
                                    }
                                }
                                
                                // Get next() from inner iterator
                                if (innerIterator != null)
                                {
                                    var nextFnVal = innerIterator.Get("next", context);
                                    if (nextFnVal.IsFunction)
                                    {
                                        delegatedIterator = innerIterator;
                                        delegatedNextFn = nextFnVal.AsFunction();
                                        stmtIndex--; // Re-evaluate this statement on next call
                                        
                                        // Get the first value from delegated iterator
                                        var firstResult = delegatedNextFn.Invoke(Array.Empty<FenValue>(), context);
                                        if (firstResult.IsObject)
                                        {
                                            var firstObj = firstResult.AsObject() as FenObject;
                                            var isDone = firstObj?.Get("done", context).ToBoolean() ?? true;
                                            if (isDone)
                                            {
                                                delegatedIterator = null;
                                                delegatedNextFn = null;
                                                stmtIndex++;
                                                continue;
                                            }
                                            return firstResult;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (result.Type == JsValueType.ReturnValue)
                    {
                        done = true;
                        var iterResult = new FenObject();
                        iterResult.Set("value", result.InnerValue);
                        iterResult.Set("done", FenValue.FromBoolean(true));
                        return FenValue.FromObject(iterResult);
                    }

                    if (result.Type == JsValueType.Error)
                    {
                        done = true;
                        return result;
                    }
                }

                // Ran out of statements — generator is done
                done = true;
                var doneResult = new FenObject();
                doneResult.Set("value", FenValue.Undefined);
                doneResult.Set("done", FenValue.FromBoolean(true));
                return FenValue.FromObject(doneResult);
            }

            var genObj = new FenObject();

            // next(value?) — resume execution
            genObj.Set("next", FenValue.FromFunction(new FenFunction("next", (FenValue[] nextArgs, FenValue thisVal) =>
            {
                if (done)
                {
                    var r = new FenObject();
                    r.Set("value", FenValue.Undefined);
                    r.Set("done", FenValue.FromBoolean(true));
                    return FenValue.FromObject(r);
                }
                return RunUntilYield();
            })));

            // return(value?) — force completion
            genObj.Set("return", FenValue.FromFunction(new FenFunction("return", (FenValue[] retArgs, FenValue thisVal) =>
            {
                done = true;
                var val = retArgs.Length > 0 ? retArgs[0] : FenValue.Undefined;
                var r = new FenObject();
                r.Set("value", val);
                r.Set("done", FenValue.FromBoolean(true));
                return FenValue.FromObject(r);
            })));

            // throw(error) — throw into generator
            genObj.Set("throw", FenValue.FromFunction(new FenFunction("throw", (FenValue[] throwArgs, FenValue thisVal) =>
            {
                done = true;
                return throwArgs.Length > 0 ? throwArgs[0] : FenValue.FromError("Generator throw");
            })));

            // Symbol.iterator — generators are their own iterators
            genObj.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (FenValue[] a, FenValue t) =>
            {
                return FenValue.FromObject(genObj);
            })));

            return FenValue.FromObject(genObj);
        }

        private FenEnvironment ExtendFunctionEnv(FenFunction fn, List<FenValue> args, IExecutionContext context, FenValue thisContext = default)
        {
            var env = new FenEnvironment(fn.Env);
            if (fn.LocalMap != null) env.InitializeFastStore(fn.LocalMap.Count);
            
            // Bind 'this'
            if (thisContext != null && !thisContext.IsNull && !thisContext.IsUndefined)
            {
                env.Set("this", thisContext);
            }
            else
            {
                // In strict mode, 'this' is undefined if not provided
                // In non-strict mode, 'this' is the global object
                if (context.StrictMode)
                {
                    env.Set("this", FenValue.Undefined);
                }
                else
                {
                    // Non-strict mode: use global object (the outer environment's 'this' or globalThis)
                    var globalThis = context.Environment?.Get("globalThis") ?? FenValue.Undefined;
                    env.Set("this", globalThis.IsUndefined ? thisContext : globalThis);
                }
            }
            if (fn.LocalMap != null && fn.LocalMap.TryGetValue("this", out int thisIdx)) env.SetFast(thisIdx, env.Get("this"));

            // Create 'arguments' object for non-arrow functions
            // Arrow functions do NOT have their own arguments object
            if (!fn.IsArrowFunction)
            {
                var argumentsObj = new FenObject();
                // ES5.1 7A: arguments object has [[Class]] = "Arguments"
                argumentsObj.InternalClass = "Arguments";
                
                for (int i = 0; i < args.Count; i++)
                {
                    argumentsObj.Set(i.ToString(), args[i], context);
                }
                argumentsObj.Set("length", FenValue.FromNumber(args.Count), context);
                // ES5.1: arguments.callee points to the current function (deprecated but required for compat)
                argumentsObj.Set("callee", FenValue.FromFunction(fn), context);
                
                // ES5.1 7A: Store parameter info for potential live mapping
                // (Full live binding would require getter/setter but this enables Object.prototype.toString)
                var paramNames = new FenObject();
                for (int i = 0; i < fn.Parameters.Count && i < args.Count; i++)
                {
                    if (!fn.Parameters[i].IsRest)
                    {
                        paramNames.Set(i.ToString(), FenValue.FromString(fn.Parameters[i].Value), context);
                    }
                }
                argumentsObj.Set("__paramNames__", FenValue.FromObject(paramNames), context);
                
                env.Set("arguments", FenValue.FromObject(argumentsObj));
            }
            if (fn.LocalMap != null && fn.LocalMap.TryGetValue("arguments", out int argsIdx)) env.SetFast(argsIdx, env.Get("arguments"));

            for (int i = 0; i < fn.Parameters.Count; i++)
            {
                var param = fn.Parameters[i];
                
                if (param.IsRest)
                {
                    var restArray = new FenObject();
                    int restIndex = 0;
                    for (int j = i; j < args.Count; j++)
                    {
                        restArray.Set(restIndex.ToString(), args[j], context);
                        restIndex++;
                    }
                    restArray.Set("length", FenValue.FromNumber(restIndex), context);
                    env.Set(param.Value, FenValue.FromObject(restArray));
                    break; // Rest parameter must be last
                }

                var argVal = i < args.Count ? args[i] : FenValue.Undefined;
                if (argVal.IsUndefined && param.DefaultValue != null)
                    argVal = Eval(param.DefaultValue, env, context);

                // Handle destructuring parameter: function({a, b}) or function([x, y])
                if (param.DestructuringPattern != null)
                {
                    EvalDestructuringAssignment(param.DestructuringPattern, argVal, env, context, isDeclaration: true);
                }
                else
                {
                    env.Set(param.Value, argVal);
                    if (fn.LocalMap != null && fn.LocalMap.TryGetValue(param.Value, out int pIdx2)) env.SetFast(pIdx2, argVal);
                }
            }

            return env;
        }

        private FenValue UnwrapReturnValue(FenValue obj)
        {
            if (obj.Type == JsValueType.ReturnValue)
            {
                return (FenValue)obj.ToNativeObject(); // Since we boxed it in FromReturnValue
            }
            return obj;
        }

        private bool IsTruthy(FenValue obj)
        {
            return obj.ToBoolean();
        }

        private bool IsError(FenValue obj)
        {
            return obj.Type == JsValueType.Error;
        }

        // Evaluate template literals: `Hello ${name}!`
        private IValue EvalTemplateLiteral(TemplateLiteral tmpl, FenEnvironment env, IExecutionContext context)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < tmpl.Quasis.Count; i++)
            {
                sb.Append(tmpl.Quasis[i].Value);
                if (i < tmpl.Expressions.Count)
                {
                    var val = Eval(tmpl.Expressions[i], env, context);
                    if (IsError(val)) return val;
                    sb.Append(val.AsString());
                }
            }
            return FenValue.FromString(sb.ToString());
        }

        // Evaluate tagged template literals: tag`Hello ${name}!`
        // The tag function receives (stringsArray, ...interpolatedValues)
        private IValue EvalTaggedTemplate(TaggedTemplateExpression taggedExpr, FenEnvironment env, IExecutionContext context)
        {
            // Evaluate the tag function
            var tagFn = Eval(taggedExpr.Tag, env, context); // Corrected property from TagName to Tag
            if (IsError(tagFn)) return tagFn;
            
            if (!tagFn.IsFunction)
            {
                return FenValue.FromError($"Tagged template: '{taggedExpr.Tag.String()}' is not a function");
            }
            
            // Create the strings array (first argument)
            var stringsArray = new FenObject();
            for (int i = 0; i < taggedExpr.Strings.Count; i++)
            {
                stringsArray.Set(i.ToString(), FenValue.FromString(taggedExpr.Strings[i]), context);
            }
            stringsArray.Set("length", FenValue.FromNumber(taggedExpr.Strings.Count), context);
            
            // Add the 'raw' property (same as strings for now, could handle escapes differently)
            var rawArray = new FenObject();
            for (int i = 0; i < taggedExpr.Strings.Count; i++)
            {
                rawArray.Set(i.ToString(), FenValue.FromString(taggedExpr.Strings[i]), context);
            }
            rawArray.Set("length", FenValue.FromNumber(taggedExpr.Strings.Count), context);
            stringsArray.Set("raw", FenValue.FromObject(rawArray), context);
            
            // Build the arguments list: [stringsArray, ...evaluatedExpressions]
            var args = new List<FenValue>();
            args.Add(FenValue.FromObject(stringsArray));
            
            // Evaluate each interpolated expression
            foreach (var expr in taggedExpr.Expressions)
            {
                var val = Eval(expr, env, context);
                if (IsError(val)) return val;
                args.Add(val);
            }
            
            // Call the tag function
            return ApplyFunction(tagFn, args, context);
        }

        private IValue EvalMemberExpression(MemberExpression me, FenEnvironment env, IExecutionContext context)
        {
            var left = Eval(me.Object, env, context);
            if (IsError(left)) return left;

            if (left.IsString)
            {
                if (me.Property == "length")
                {
                    return FenValue.FromNumber(left.ToString().Length);
                }

                // Lookup in StringPrototype
                var proto = GetStringPrototype();
                var val = proto.Get(me.Property, context);
                if (val != null) return val;
            }
            else if (left.IsBigInt)
            {
                // Handle BigInt primitive methods (toString, valueOf, etc.)
                if (me.Property == "toString")
                {
                    return FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
                    {
                        var bigInt = left.AsBigInt();
                        if (bigInt == null) return FenValue.FromString("0");
                        return FenValue.FromString(bigInt.ToStringWithoutSuffix());
                    }));
                }
                else if (me.Property == "valueOf")
                {
                    return FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
                    {
                        return left;
                    }));
                }
            }
            else if (left.IsObject)
            {
                var obj = left.AsObject();
                if (obj != null)
                {
                    var val = obj.Get(me.Property, context);
                    if (val != null) return val;
                }
            }

            return FenValue.Undefined;
        }

        private IValue EvalIndexExpression(IndexExpression ie, FenEnvironment env, IExecutionContext context)
        {
            var left = Eval(ie.Left, env, context);
            if (IsError(left)) return left;

            var index = Eval(ie.Index, env, context);
            if (IsError(index)) return index;

            if (left.IsObject)
            {
                var obj = left.AsObject();
                if (obj != null)
                {
                    var val = obj.Get(index.ToString(), context);
                    if (val != null) return val;
                }
            }

            return FenValue.Undefined;
        }

        private IValue EvalNewExpression(NewExpression node, FenEnvironment env, IExecutionContext context)
        {
            var function = Eval(node.Constructor, env, context);
            if (IsError(function)) return function;

            var args = EvalExpressionsWithSpread(node.Arguments, env, context);
            if (args.Count == 1 && IsError(args[0])) return args[0];

            var fn = function.AsFunction();
            if (fn  == null)
            {
                return FenValue.FromError($"not a constructor: {function.Type}");
            }

            // Create new instance
            var instance = new FenObject();
            if (fn.Prototype != null)
            {
                instance.SetPrototype(fn.Prototype);
            }

            if (fn.IsNative)
            {
               try
               {
                   var res = fn.NativeImplementation(args.ToArray(), FenValue.FromObject(instance));
                   if (res.IsObject || res.IsFunction) return res;  // Return object or function from native constructor
                   return FenValue.FromObject(instance);
               }
               catch (Exception ex)
               {
                   return FenValue.FromError(ex.Message);
               }
            }
            
            // Create new environment for the constructor call
            var newEnv = new FenEnvironment(fn.Env);
            
            // Bind 'this' to the new instance
            newEnv.Set("this", FenValue.FromObject(instance));

            // Initialize class fields (including private fields) BEFORE running constructor
            if (fn.FieldDefinitions != null)
            {
                foreach (var (fieldName, isPrivate, isStatic, initializer) in fn.FieldDefinitions)
                {
                    if (!isStatic)
                    {
                        IValue fieldValue = FenValue.Undefined;
                        if (initializer != null)
                        {
                            fieldValue = Eval(initializer, newEnv, context);
                            if (IsError(ToFenValue(fieldValue))) return ToFenValue(fieldValue);
                        }
                        instance.Set(fieldName, ToFenValue(fieldValue));
                    }
                }
            }

            // Bind arguments
            for (int i = 0; i < fn.Parameters.Count; i++)
            {
                if (i < args.Count)
                {
                    newEnv.Set(fn.Parameters[i].Value, args[i]);
                }
                else
                {
                    newEnv.Set(fn.Parameters[i].Value, FenValue.Undefined);
                }
            }

            // Execute body
            context.PushCallFrame(fn.Name ?? "constructor");
            
            // ES2015 new.target: Set NewTarget to the constructor function
            var oldNewTarget = context.NewTarget;
            context.NewTarget = function;
            
            var result = Eval(fn.Body, newEnv, context);
            
            context.NewTarget = oldNewTarget;
            context.PopCallFrame();

            if (IsError(result)) return result;

            // If constructor returns an object OR function, return it. Otherwise return 'this' (instance)
            // This is important for Proxy constructor which may return a function when target is a function
            var returnValue = UnwrapReturnValue(result);
            if (returnValue.IsObject || returnValue.IsFunction)
            {
                return returnValue;
            }

            return FenValue.FromObject(instance);
        }

        private IValue EvalThrowStatement(ThrowStatement ts, FenEnvironment env, IExecutionContext context)
        {
            var val = Eval(ts.Value, env, context);
            if (IsError(val)) return val;

            return FenValue.FromError(val.ToString());
        }

        private IValue EvalTryStatement(TryStatement ts, FenEnvironment env, IExecutionContext context)
        {
            FenValue result = FenValue.Undefined;
            IValue controlFlowValue = null; // Store return/break/continue for later

            try
            {
                result = Eval(ts.Block, env, context);

                // Check if result is a control flow value (return/break/continue)
                if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Break || result.Type == JsValueType.Continue)
                {
                    controlFlowValue = result;
                    result = FenValue.Undefined;
                }
            }
            catch (Exception ex)
            {
                // Handle .NET exceptions as ErrorValue
                result = FenValue.FromError(ex.Message);
            }

            // Catch block handling - ES5.1: throw can throw any value
            if (result.Type == JsValueType.Error && ts.CatchBlock != null)
            {
                var catchEnv = new FenEnvironment(env);
                if (ts.CatchParameter != null)
                {
                    // ES5.1 1E: Support catching non-Error values
                    // If it's an Error type from FenValue, create error object
                    // Otherwise, the thrown value should be passed as-is
                    var errorObj = new FenObject();
                    errorObj.InternalClass = "Error";
                    errorObj.Set("message", FenValue.FromString(result.AsError()), null);
                    errorObj.Set("name", FenValue.FromString("Error"), null);
                    errorObj.Set("stack", FenValue.FromString($"Error: {result.AsError()}\n    at <anonymous>"), null);
                    catchEnv.Set(ts.CatchParameter.Value, FenValue.FromObject(errorObj));
                }
                result = Eval(ts.CatchBlock, catchEnv, context);
                
                // Check for control flow in catch block
                if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Break || result.Type == JsValueType.Continue)
                {
                    controlFlowValue = result;
                    result = FenValue.Undefined;
                }
            }

            // Finally block ALWAYS runs
            if (ts.FinallyBlock != null)
            {
                var finallyResult = Eval(ts.FinallyBlock, env, context);
                
                // If finally has its own control flow, it takes precedence
                if (finallyResult.Type == JsValueType.ReturnValue || finallyResult.Type == JsValueType.Break || finallyResult.Type == JsValueType.Continue)
                {
                    return finallyResult;
                }
            }

            // Restore any saved control flow value
            if (controlFlowValue != null)
            {
                return controlFlowValue;
            }

            return result;
        }

        // ES5.1 with statement: with (obj) { ... }
        // Adds object properties to the scope chain
        private IValue EvalWithStatement(WithStatement ws, FenEnvironment env, IExecutionContext context)
        {
            var objVal = Eval(ws.Object, env, context);
            if (objVal.Type == JsValueType.Error) return objVal;
            
            if (!objVal.IsObject)
            {
                return FenValue.FromError("with statement requires an object");
            }
            
            var obj = objVal.AsObject();
            
            // Create a new environment that first looks up properties on the object
            var withEnv = new FenEnvironment(env);
            
            // Copy object properties to the environment for lookup
            if (obj != null)
            {
                foreach (var key in obj.Keys())
                {
                    withEnv.Set(key, obj.Get(key, null));
                }
            }
            
            // Evaluate the body in the with environment
            if (ws.Body is BlockStatement block)
            {
                return EvalBlockStatement(block, withEnv, context);
            }
            else if (ws.Body != null)
            {
                return Eval(ws.Body, withEnv, context);
            }
            
            return FenValue.Undefined;
        }

        private IValue EvalWhileStatement(WhileStatement ws, FenEnvironment env, IExecutionContext context, string label = null)
        {
            FenValue result = FenValue.Null;

            while (true)
            {
                var condition = Eval(ws.Condition, env, context);
                if (IsError(condition)) return condition;

                if (!IsTruthy(condition))
                {
                    break;
                }

                result = Eval(ws.Body, env, context);

                if (result != null)
                {
                    if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                    if (result.Type == JsValueType.Break)
                    {
                        if (result.BreakContinueLabel != null)
                        {
                            if (result.BreakContinueLabel == label) return FenValue.Null;
                            return result;
                        }
                        return FenValue.Null;
                    }
                    if (result.Type == JsValueType.Continue)
                    {
                        if (result.BreakContinueLabel != null)
                        {
                            if (result.BreakContinueLabel == label) continue;
                            return result;
                        }
                        continue;
                    }
                }
            }

            return result;
        }

        private IValue EvalForStatement(ForStatement fs, FenEnvironment env, IExecutionContext context, string label = null)
        {
            var loopEnv = new FenEnvironment(env); // Scope for 'let' variables in init

            if (fs.Init != null)
            {
                var init = Eval(fs.Init, loopEnv, context);
                if (IsError(init)) return init;
            }

            FenValue result = FenValue.Null;

            while (true)
            {
                if (fs.Condition != null)
                {
                    var condition = Eval(fs.Condition, loopEnv, context);
                    if (IsError(condition)) return condition;
                    if (!IsTruthy(condition)) break;
                }

                result = Eval(fs.Body, loopEnv, context);

                if (!result.IsUndefined && !result.IsNull)
                {
                    if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                    if (result.Type == JsValueType.Break)
                    {
                        if (result.BreakContinueLabel != null)
                        {
                            if (result.BreakContinueLabel == label) return FenValue.Null;
                            return result;
                        }
                        return FenValue.Null;
                    }
                    if (result.Type == JsValueType.Continue && result.BreakContinueLabel != null)
                    {
                        if (result.BreakContinueLabel == label) { /* fall through to update */ }
                        else return result;
                    }
                    // Unlabeled continue falls through to update
                }

                if (fs.Update != null)
                {
                    var update = Eval(fs.Update, loopEnv, context);
                    if (IsError(update)) return update;
                }
            }

            return result;
        }

        // Evaluate for-in: for (x in obj) { ... }
        // ES5.1: Enumerates own AND prototype chain enumerable properties
        private IValue EvalForInStatement(ForInStatement fs, FenEnvironment env, IExecutionContext context, string label = null)
        {
            var loopEnv = new FenEnvironment(env);
            var objVal = Eval(fs.Object, env, context);
            if (IsError(objVal)) return objVal;

            FenValue result = FenValue.Null;

            // If object, iterate over its keys including prototype chain
            if (objVal.IsObject)
            {
                var obj = objVal.AsObject();
                if (obj != null)
                {
                    // ES5.1: Collect all enumerable properties from prototype chain
                    var seenKeys = new HashSet<string>();
                    var currentObj = obj;
                    
                    while (currentObj != null)
                    {
                        foreach (var key in currentObj.Keys())
                        {
                            if (seenKeys.Contains(key)) continue; // Skip shadowed properties
                            seenKeys.Add(key);
                            
                            // Set the loop variable to current key
                            if (fs.DestructuringPattern != null)
                                EvalDestructuringAssignment(fs.DestructuringPattern, FenValue.FromString(key), loopEnv, context, isDeclaration: true);
                            else
                                loopEnv.Set(fs.Variable.Value, FenValue.FromString(key));

                            result = Eval(fs.Body, loopEnv, context);

                            if (!result.IsUndefined && !result.IsNull)
                            {
                                if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                                if (result.Type == JsValueType.Break)
                                {
                                    if (result.BreakContinueLabel != null)
                                    {
                                        if (result.BreakContinueLabel == label) return FenValue.Null;
                                        return result;
                                    }
                                    return FenValue.Null;
                                }
                                if (result.Type == JsValueType.Continue)
                                {
                                    if (result.BreakContinueLabel != null)
                                    {
                                        if (result.BreakContinueLabel == label) continue;
                                        return result;
                                    }
                                    continue;
                                }
                            }
                        }
                        currentObj = currentObj.GetPrototype(); // Traverse prototype chain
                    }
                }
            }

            return result;
        }

        // Evaluate for-of: for (x of iterable) { ... }
        // Implements the full iterator protocol: obj[Symbol.iterator]().next()
        private IValue EvalForOfStatement(ForOfStatement fs, FenEnvironment env, IExecutionContext context, string label = null)
        {
            var loopEnv = new FenEnvironment(env);
            var iterableVal = Eval(fs.Iterable, env, context);
            if (IsError(iterableVal)) return iterableVal;

            FenValue result = FenValue.Null;

            // Helper to set the loop variable (supports destructuring)
            void SetLoopVar(FenValue val)
            {
                if (fs.DestructuringPattern != null)
                    EvalDestructuringAssignment(fs.DestructuringPattern, val, loopEnv, context, isDeclaration: true);
                else
                    loopEnv.Set(fs.Variable.Value, val);
            }

            // Helper to check loop control flow
            // Returns: 0 = continue loop, 1 = break loop normally, 2 = propagate result
            int CheckControl(FenValue r)
            {
                if (r.IsUndefined || r.IsNull) return 0;
                if (r.Type == JsValueType.ReturnValue || r.Type == JsValueType.Error) return 2;
                if (r.Type == JsValueType.Break)
                {
                    if (r.BreakContinueLabel != null)
                        return r.BreakContinueLabel == label ? 1 : 2;
                    return 1;
                }
                if (r.Type == JsValueType.Continue)
                {
                    if (r.BreakContinueLabel != null)
                        return r.BreakContinueLabel == label ? 0 : 2;
                    return 0;
                }
                return 0;
            }

            // Handle strings first (iterate over characters)
            if (iterableVal.IsString)
            {
                string str = iterableVal.ToString();
                foreach (char c in str)
                {
                    SetLoopVar(FenValue.FromString(c.ToString()));
                    result = Eval(fs.Body, loopEnv, context);
                    int ctrl = CheckControl(result);
                    if (ctrl == 2) return result;
                    if (ctrl == 1) return FenValue.Null;
                }
                return result;
            }

            // Handle objects with iterator protocol
            if (iterableVal.IsObject)
            {
                var obj = iterableVal.AsObject();
                if (obj != null)
                {
                    // Check for Symbol.iterator or Symbol.asyncIterator method
                    IValue iteratorMethod = null;
                    
                    // ES2018: For 'for await...of', prefer Symbol.asyncIterator
                    if (fs.IsAwait)
                    {
                        var asyncIteratorMethod = obj.Get("[Symbol.asyncIterator]");
                        if (asyncIteratorMethod != null && asyncIteratorMethod.IsFunction)
                            iteratorMethod = asyncIteratorMethod;
                    }

                    // Fall back to Symbol.iterator (or use it if not async)
                    if (iteratorMethod == null)
                    {
                        var symbolIteratorMethod = obj.Get("[Symbol.iterator]");
                        if (symbolIteratorMethod != null && symbolIteratorMethod.IsFunction)
                            iteratorMethod = symbolIteratorMethod;
                    }

                    if (iteratorMethod == null)
                    {
                        var altIterator = obj.Get("__iterator__");
                        if (altIterator != null && altIterator.IsFunction)
                            iteratorMethod = altIterator;
                    }

                    // If we have an iterator method, use the full protocol
                    if (iteratorMethod != null && iteratorMethod.IsFunction)
                    {
                        var iterator = ApplyFunction(FenValue.FromFunction(iteratorMethod.AsFunction()), new List<FenValue>(), context, ToFenValue(iterableVal));
                        if (IsError(iterator)) return iterator;

                        if (iterator.IsObject)
                        {
                            var iterObj = iterator.AsObject();
                            var nextMethod = iterObj?.Get("next");

                            if (nextMethod != null && nextMethod.Value.IsFunction)
                            {
                                while (true)
                                {
                                    var iterResult = ApplyFunction(FenValue.FromFunction(nextMethod.Value.AsFunction()), new List<FenValue>(), context, iterator);
                                    if (IsError(iterResult)) return iterResult;

                                    if (iterResult.IsObject)
                                    {
                                        var resultObj = iterResult.AsObject();
                                        var doneVal = resultObj?.Get("done");
                                        var valueVal = resultObj?.Get("value");

                                        if (doneVal != null && IsTruthy(doneVal.Value)) break;

                                        SetLoopVar(valueVal ?? FenValue.Undefined);
                                        result = Eval(fs.Body, loopEnv, context);
                                        int ctrl = CheckControl(result);
                                        if (ctrl == 2) return result;
                                        if (ctrl == 1) return FenValue.Null;
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                                return result;
                            }
                        }
                    }

                    // Fallback for array-like objects (has length property and numeric keys)
                    if (obj.Has("length"))
                    {
                        var lenVal = obj.Get("length");
                        if (lenVal.IsNumber)
                        {
                            int len = (int)lenVal.ToNumber();
                            for (int i = 0; i < len; i++)
                            {
                                var val = obj.Get(i.ToString());
                                SetLoopVar(val);
                                result = Eval(fs.Body, loopEnv, context);
                                int ctrl = CheckControl(result);
                                if (ctrl == 2) return result;
                                if (ctrl == 1) return FenValue.Null;
                            }
                            return result;
                        }
                    }

                    // Plain objects without length are not iterable
                    return FenValue.FromError($"{iterableVal} is not iterable");
                }
            }

            return FenValue.FromError($"{iterableVal} is not iterable");
        }

        // Evaluate ternary conditional: condition ? consequent : alternate
        private IValue EvalConditionalExpression(ConditionalExpression ce, FenEnvironment env, IExecutionContext context)
        {
            var condition = Eval(ce.Condition, env, context);
            if (IsError(condition)) return condition;

            if (IsTruthy(condition))
            {
                return Eval(ce.Consequent, env, context);
            }
            else
            {
                return Eval(ce.Alternate, env, context);
            }
        }

        // Evaluate arrow function: (params) => body - creates a FenFunction
        private IValue EvalArrowFunctionExpression(ArrowFunctionExpression ae, FenEnvironment env)
        {
            // Arrow functions capture the enclosing environment just like regular functions
            // They do NOT have their own `arguments` object
            var fn = new FenFunction(ae.Parameters, ae.Body, env);
            fn.IsArrowFunction = true;
            return FenValue.FromFunction(fn);
        }

        // Evaluate switch statement
        private IValue EvalSwitchStatement(SwitchStatement ss, FenEnvironment env, IExecutionContext context)
        {
            var discriminant = Eval(ss.Discriminant, env, context);
            if (IsError(discriminant)) return discriminant;

            SwitchCase match = null;
            bool foundMatch = false;

            // Find matching case
            foreach (var c in ss.Cases)
            {
                if (c.Test  == null) continue; // Skip default for now

                var test = Eval(c.Test, env, context);
                if (IsError(test)) return test;

                if (discriminant.StrictEquals(test))
                {
                    match = c;
                    foundMatch = true;
                    break;
                }
            }

            // If no match, look for default
            if (!foundMatch)
            {
                foreach (var c in ss.Cases)
                {
                    if (c.Test  == null)
                    {
                        match = c;
                        break;
                    }
                }
            }

            // Execute cases
            if (match != null)
            {
                FenValue result = FenValue.Null;
                int index = ss.Cases.IndexOf(match);
                
                for (int i = index; i < ss.Cases.Count; i++)
                {
                    var currentCase = ss.Cases[i];
                    foreach (var stmt in currentCase.Consequent)
                    {
                        result = Eval(stmt, env, context);
                        
                        if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                        if (result.Type == JsValueType.Break) return FenValue.Null; // Break exits switch
                        if (result.Type == JsValueType.Continue) return FenValue.FromError("Illegal continue statement: no surrounding loop");
                    }
                }
                return result;
            }

            return FenValue.Null;
        }

        // Evaluate do-while loop
        private IValue EvalDoWhileStatement(DoWhileStatement ds, FenEnvironment env, IExecutionContext context, string label = null)
        {
            FenValue result = FenValue.Null;
            var loopEnv = new FenEnvironment(env);

            do
            {
                result = Eval(ds.Body, loopEnv, context);

                if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                if (result.Type == JsValueType.Break)
                {
                    if (result.BreakContinueLabel != null)
                    {
                        if (result.BreakContinueLabel == label) return FenValue.Null;
                        return result;
                    }
                    return FenValue.Null;
                }
                if (result.Type == JsValueType.Continue)
                {
                    if (result.BreakContinueLabel != null)
                    {
                        if (result.BreakContinueLabel == label) { /* fall through to condition */ }
                        else return result;
                    }
                    // Unlabeled continue goes to condition check
                }

                var condition = Eval(ds.Condition, loopEnv, context);
                if (IsError(condition)) return condition;
                if (!IsTruthy(condition)) break;

            } while (true);

            return result;
        }

        private IValue EvalLabeledStatement(LabeledStatement stmt, FenEnvironment env, IExecutionContext context)
        {
            string label = stmt.Label.Value;
            IValue result;

            // Pass the label into loop evaluators so they can catch labeled break/continue
            switch (stmt.Body)
            {
                case ForStatement forStmt:
                    result = EvalForStatement(forStmt, env, context, label);
                    break;
                case WhileStatement whileStmt:
                    result = EvalWhileStatement(whileStmt, env, context, label);
                    break;
                case DoWhileStatement doWhileStmt:
                    result = EvalDoWhileStatement(doWhileStmt, env, context, label);
                    break;
                case ForOfStatement forOfStmt:
                    result = EvalForOfStatement(forOfStmt, env, context, label);
                    break;
                case ForInStatement forInStmt:
                    result = EvalForInStatement(forInStmt, env, context, label);
                    break;
                default:
                    result = Eval(stmt.Body, env, context);
                    break;
            }

            // If we get a labeled break targeting this label, consume it
            if (result is FenValue fv && fv.Type == JsValueType.Break && fv.BreakContinueLabel == label)
            {
                return FenValue.Null;
            }

            return result;
        }

        private IValue EvalDestructuringAssignment(Expression pattern, FenValue value, FenEnvironment env, IExecutionContext context, bool isDeclaration = false, bool isConst = false)
        {
            // Helper: bind a name to a value, respecting declaration vs assignment semantics
            void Bind(string name, FenValue val)
            {
                if (isDeclaration)
                {
                    if (isConst) env.SetConst(name, val);
                    else env.Set(name, val);
                }
                else
                {
                    env.Update(name, val);
                }
            }

            if (pattern is ArrayLiteral arrayPattern)
            {
                // Array destructuring: [a, b] = [1, 2]
                if (value.IsObject)
                {
                    var obj = value.AsObject();
                    if (obj != null)
                    {
                        // Handle array-like access
                        for (int i = 0; i < arrayPattern.Elements.Count; i++)
                        {
                            var element = arrayPattern.Elements[i];
                            var val = obj.Get(i.ToString());

                            if (element is Identifier ident)
                            {
                                Bind(ident.Value, val);
                            }
                            else if (element is AssignmentExpression assign)
                            {
                                // Default value: [a = 1]
                                if (val.IsUndefined)
                                    val = Eval(assign.Right, env, context);
                                if (assign.Left is Identifier leftIdent)
                                    Bind(leftIdent.Value, val);
                                else
                                    EvalDestructuringAssignment(assign.Left, val, env, context, isDeclaration, isConst);
                            }
                            else if (element is SpreadElement spread)
                            {
                                // Rest: [...rest]
                                if (spread.Argument is Identifier restIdent)
                                {
                                    var restArray = new FenObject();
                                    int restIndex = 0;
                                    if (obj.Has("length"))
                                    {
                                        var lenVal = obj.Get("length");
                                        if (lenVal.IsNumber)
                                        {
                                            int len = (int)lenVal.ToNumber();
                                            for (int j = i; j < len; j++)
                                            {
                                                restArray.Set(restIndex.ToString(), obj.Get(j.ToString()));
                                                restIndex++;
                                            }
                                            restArray.Set("length", FenValue.FromNumber(restIndex));
                                        }
                                    }
                                    Bind(restIdent.Value, FenValue.FromObject(restArray));
                                }
                                // Spread must be last, so break
                                break;
                            }
                            else if (element is ArrayLiteral || element is ObjectLiteral)
                            {
                                // Nested destructuring
                                EvalDestructuringAssignment(element, val, env, context, isDeclaration, isConst);
                            }
                        }
                    }
                }
                return value;
            }
            else if (pattern is ObjectLiteral objectPattern)
            {
                // Object destructuring: {a, b} = {a: 1, b: 2}
                if (value.IsObject)
                {
                    var obj = value.AsObject();
                    if (obj != null)
                    {
                        var usedKeys = new System.Collections.Generic.HashSet<string>();
                        foreach (var pair in objectPattern.Pairs)
                        {
                            var key = pair.Key;
                            var target = pair.Value;

                            // Rest element: {a, ...rest}
                            if (key.StartsWith("__spread_") && target is SpreadElement restSpread)
                            {
                                if (restSpread.Argument is Identifier restIdent)
                                {
                                    var restObj = new FenObject();
                                    foreach (var k in obj.Keys())
                                        if (!usedKeys.Contains(k))
                                            restObj.Set(k, obj.Get(k, null), null);
                                    Bind(restIdent.Value, FenValue.FromObject(restObj));
                                }
                                continue;
                            }

                            usedKeys.Add(key);
                            var val = obj.Get(key);

                            if (target is Identifier ident)
                            {
                                Bind(ident.Value, val);
                            }
                            else if (target is AssignmentExpression assign)
                            {
                                // Default value: {a = 1} or rename with default: {a: b = 1}
                                if (val.IsUndefined)
                                    val = Eval(assign.Right, env, context);
                                if (assign.Left is Identifier leftIdent)
                                    Bind(leftIdent.Value, val);
                                else
                                    EvalDestructuringAssignment(assign.Left, val, env, context, isDeclaration, isConst);
                            }
                            else if (target is ArrayLiteral || target is ObjectLiteral)
                            {
                                // Nested destructuring: {a: [x, y]} or {a: {b}}
                                EvalDestructuringAssignment(target, val, env, context, isDeclaration, isConst);
                            }
                        }
                    }
                }
                return value;
            }

            return value;
        }

        private IValue EvalClassStatement(ClassStatement stmt, FenEnvironment env, IExecutionContext context)
        {
            // Create a constructor function
            // If no constructor is defined, create a default one
            FenFunction constructor = null;
            var constructorDef = stmt.Methods.FirstOrDefault(m => m.Kind == "constructor");
            
            if (constructorDef != null)
            {
                constructor = new FenFunction(constructorDef.Value.Parameters, constructorDef.Value.Body, env);
            }
            else
            {
                // Default constructor: function() {} or function(...args) { super(...args); }
                // For now, simple empty constructor
                constructor = new FenFunction(new List<Identifier>(), new BlockStatement(), env);
            }

            // Create the prototype object
            var prototype = new FenObject();
            
            // Handle inheritance (basic)
            if (stmt.SuperClass != null)
            {
                var superClass = Eval(stmt.SuperClass, env, context);
                if (superClass.IsObject)
                {
                    // In a real JS engine, we'd set the prototype chain correctly
                    // For now, let's just copy methods or link prototypes if we had a robust prototype system
                    // This is a placeholder for inheritance
                }
            }

            // Add methods to prototype
            foreach (var method in stmt.Methods)
            {
                if (method.Kind == "constructor") continue;
                
                if (method.Static)
                {
                    // Static methods go on the constructor function object itself
                    // But FenFunction isn't fully a FenObject yet in this implementation
                    // We might need to attach them differently or upgrade FenFunction
                }
                else
                {
                    // Instance methods go on the prototype
                    // Private methods are prefixed with #
                    var methodFunc = new FenFunction(method.Value.Parameters, method.Value.Body, env);
                    string methodName = method.IsPrivate ? "#" + method.Key.Value : method.Key.Value;
                    prototype.Set(methodName, FenValue.FromFunction(methodFunc));
                }
            }

            // 1. Initialize Static Fields & Execute Static Blocks (Interleaved order theoretically, but for now phase 1: fields, phase 2: blocks)
            // Ideally we should merge Properties and StaticBlocks based on source order.
            // Simplified: Initialize static fields first
            foreach (var prop in stmt.Properties)
            {
                if (prop.Static)
                {
                    IValue val = FenValue.Undefined;
                    if (prop.Value != null)
                    {
                        var staticEnv = new FenEnvironment(env);
                        staticEnv.Set("this", FenValue.FromFunction(constructor));
                        val = Eval(prop.Value, staticEnv, context);
                    }
                    
                    if (prop.IsPrivate)
                    {
                         constructor.Set("#" + prop.Key.String(), (FenValue)val);
                    }
                    else
                    {
                        constructor.Set(prop.Key.String(), (FenValue)val);
                    }
                }
            }

            // 2. Execute Static Blocks
            if (stmt.StaticBlocks.Count > 0)
            {
                 var staticBlockEnv = new FenEnvironment(env);
                 staticBlockEnv.Set("this", FenValue.FromFunction(constructor));
                 
                 foreach (var block in stmt.StaticBlocks)
                 {
                     Eval(block.Body, staticBlockEnv, context);
                 }
            }

            // 3. Store INSTANCE field definitions on the constructor for initialization during `new`
            var fieldDefinitions = new List<(string name, bool isPrivate, bool isStatic, Expression initializer)>();
            foreach (var prop in stmt.Properties)
            {
                if (!prop.Static)
                {
                    string fieldName = prop.IsPrivate ? "#" + prop.Key.String() : prop.Key.String();
                    fieldDefinitions.Add((fieldName, prop.IsPrivate, prop.Static, prop.Value));
                }
            }
            
            // Store the field definitions on the constructor
            constructor.FieldDefinitions = fieldDefinitions;

            // Link constructor and prototype
            constructor.Prototype = prototype;
            
            // For now, let's bind the class name to the constructor function
            // Apply class decorators (if any)
            var constructorValue = FenValue.FromFunction(constructor);
            if (stmt.Decorators != null && stmt.Decorators.Count > 0)
            {
                constructorValue = ApplyDecorators(stmt.Decorators, constructorValue, context, env);
            }
            
            env.Set(stmt.Name.Value, constructorValue);
            
            return FenValue.Undefined;
        }

        private List<FenValue> EvalExpressionsWithSpread(List<Expression> exps, FenEnvironment env, IExecutionContext context)
        {
            var result = new List<FenValue>();

            foreach (var e in exps)
            {
                if (e is SpreadElement spread)
                {
                    var val = Eval(spread.Argument, env, context);
                    if (IsError(val)) return new List<FenValue> { val };

                    if (val.IsObject)
                    {
                        var obj = val.AsObject();
                        if (obj != null)
                        {
                            // If array-like (has length)
                            if (obj.Has("length"))
                            {
                                var lenVal = obj.Get("length");
                                if (lenVal.IsNumber)
                                {
                                    int len = (int)lenVal.ToNumber();
                                    for (int i = 0; i < len; i++)
                                    {
                                        result.Add(obj.Get(i.ToString()));
                                    }
                                    continue;
                                }
                            }
                        }
                    }
                    // If not array-like, just add the value itself (fallback)
                    // Or should we error? Standard JS errors if not iterable.
                    // For now, let's just add it to avoid crashing.
                    result.Add(val);
                }
                else
                {
                    var evaluated = Eval(e, env, context);
                    if (IsError(evaluated))
                    {
                        return new List<FenValue> { evaluated };
                    }
                    result.Add(evaluated);
                }
            }

            return result;
        }


        public Dictionary<string, FenValue> Exports { get; } = new Dictionary<string, FenValue>();

        private IValue EvalImportDeclaration(ImportDeclaration stmt, FenEnvironment env, IExecutionContext context)
        {
            if (context.ModuleLoader == null)
            {
                return FenValue.FromError("ModuleLoader not available. Cannot import modules.");
            }

            string path = context.ModuleLoader.Resolve(stmt.Source, context.CurrentModulePath ?? "");
            IObject moduleExports = context.ModuleLoader.LoadModule(path);

            if (moduleExports == null)
            {
                return FenValue.FromError($"Failed to load module: {stmt.Source}");
            }

            // Bind imports to local environment
            foreach (var specifier in stmt.Specifiers)
            {
                if (specifier.Imported == null || specifier.Local == null) continue;

                string importedName = specifier.Imported.Value;
                string localName = specifier.Local.Value;

                if (importedName == "*")
                {
                    // Namespace import: import * as ns from 'module'
                    // Bind the entire module exports object
                    env.Set(localName, FenValue.FromObject(moduleExports));
                }
                else
                {
                    // Named or default import: import { x } or import x from 'module'
                    if (!moduleExports.Has(importedName, context))
                    {
                        return FenValue.FromError($"SyntaxError: The requested module does not provide an export named '{importedName}'");
                    }

                    var exportedValue = moduleExports.Get(importedName);
                    // Get returns FenValue directly, no need for HasValue/Value
                    env.Set(localName, exportedValue);
                }
            }

            return FenValue.Undefined;
        }

        private IValue EvalExportDeclaration(ExportDeclaration stmt, FenEnvironment env, IExecutionContext context)
        {
            // export default expr
            if (stmt.DefaultExpression != null)
            {
                var val = Eval(stmt.DefaultExpression, env, context);
                if (IsError(val)) return val;

                Exports["default"] = ToFenValue(val);
                return FenValue.Undefined;
            }

            // export var/let/const/function/class
            if (stmt.Declaration != null)
            {
                var val = Eval(stmt.Declaration, env, context);
                if (IsError(val)) return val;

                // Extract binding names from declaration
                if (stmt.Declaration is LetStatement letStmt)
                {
                    Exports[letStmt.Name.Value] = env.Get(letStmt.Name.Value);
                }
                else if (stmt.Declaration is ClassStatement classStmt)
                {
                    Exports[classStmt.Name.Value] = env.Get(classStmt.Name.Value);
                }
                // Note: Function declarations are typically represented as LetStatements with function values

                return FenValue.Undefined;
            }

            // export { x, y } or export { x, y } from 'module'
            if (stmt.Specifiers != null && stmt.Specifiers.Count > 0)
            {
                IObject sourceModule = null;

                // Re-export from another module
                if (!string.IsNullOrEmpty(stmt.Source))
                {
                    if (context.ModuleLoader == null)
                    {
                        return FenValue.FromError("ModuleLoader not available for re-export.");
                    }

                    string path = context.ModuleLoader.Resolve(stmt.Source, context.CurrentModulePath ?? "");
                    sourceModule = context.ModuleLoader.LoadModule(path);

                    if (sourceModule == null)
                    {
                        return FenValue.FromError($"Failed to load module for re-export: {stmt.Source}");
                    }
                }

                // Bind each export
                foreach (var specifier in stmt.Specifiers)
                {
                    if (specifier.Local == null) continue;

                    string localName = specifier.Local.Value;
                    string exportedName = specifier.Exported?.Value ?? localName;

                    FenValue valueToExport;

                    if (sourceModule != null)
                    {
                        if (localName == "*")
                        {
                            if (exportedName == "*")
                            {
                                // export * from "mod"
                                foreach (var key in sourceModule.Keys())
                                {
                                    if (key == "default") continue;
                                    Exports[key] = sourceModule.Get(key);
                                }
                                continue;
                            }
                            else
                            {
                                // export * as ns from "mod"
                                valueToExport = FenValue.FromObject(sourceModule);
                            }
                        }
                        else
                        {
                            // Re-export named export
                            if (!sourceModule.Has(localName, context))
                            {
                                return FenValue.FromError($"SyntaxError: The requested module does not provide an export named '{localName}'");
                            }
                            valueToExport = sourceModule.Get(localName);
                        }
                    }
                    else
                    {
                        // Export from current scope
                        valueToExport = env.Get(localName);
                    }

                    Exports[exportedName] = valueToExport;
                }

                return FenValue.Undefined;
            }

            return FenValue.Undefined;
        }

        private IValue EvalAsyncFunctionExpression(AsyncFunctionExpression node, FenEnvironment env)
        {
            var function = new FenFunction(node.Parameters, node.Body, env);
            function.IsAsync = true;
            return FenValue.FromFunction(function);
        }

        private IValue EvalAwaitExpression(AwaitExpression node, FenEnvironment env, IExecutionContext context)
        {
            var value = Eval(node.Argument, env, context);
            if (IsError(value)) return value;

            var fv = ToFenValue(value);

            // If not a promise, wrap in resolved promise semantics (just return value)
            if (!fv.IsObject || !(fv.AsObject() is Types.JsPromise promise))
            {
                // Check if it's a thenable (object with a .then method)
                if (fv.IsObject)
                {
                    var obj = fv.AsObject();
                    var thenVal = obj?.Get("then");
                    if (thenVal.HasValue && thenVal.Value.IsFunction)
                    {
                        promise = Types.JsPromise.Resolve(fv, context);
                    }
                    else
                    {
                        return fv;
                    }
                }
                else
                {
                    return fv;
                }
            }

            // If already settled, return immediately
            if (promise.IsSettled)
            {
                if (promise.IsFulfilled) return promise.Result;
                return FenValue.FromError(promise.Result.ToString());
            }

            // Pump the microtask queue to let promise reactions run.
            // Single-threaded cooperative approach: microtasks settle promises.
            // Use the microtask queue directly to avoid phase assertion issues
            // (we may already be inside a JS execution or microtask phase).
            const int maxPumps = 5000;
            for (int i = 0; i < maxPumps; i++)
            {
                try
                {
                    EventLoop.EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
                }
                catch (InvalidOperationException)
                {
                    // Already in microtask phase — just break and return what we have
                    break;
                }
                if (promise.IsSettled) break;

                // Also process one task in case the resolution depends on a queued task
                if (EventLoop.EventLoopCoordinator.Instance.HasPendingTasks)
                {
                    try { EventLoop.EventLoopCoordinator.Instance.ProcessNextTask(); }
                    catch (InvalidOperationException) { break; }
                }

                if (promise.IsSettled) break;
            }

            if (promise.IsSettled)
            {
                if (promise.IsFulfilled) return promise.Result;
                return FenValue.FromError(promise.Result.ToString());
            }

            // Still pending after pumping — return undefined rather than blocking forever
            return FenValue.Undefined;
        }

        private FenObject _numberPrototype;
        
        private FenObject GetNumberPrototype()
        {
            if (_numberPrototype != null) return _numberPrototype;
            
            _numberPrototype = new FenObject();
            
            // Number.prototype.toFixed(digits)
            _numberPrototype.Set("toFixed", FenValue.FromFunction(new FenFunction("toFixed", (args, thisVal) =>
            {
                var num = thisVal.ToNumber();
                int digits = args.Length > 0 ? Math.Max(0, Math.Min(100, (int)args[0].ToNumber())) : 0;
                return FenValue.FromString(num.ToString($"F{digits}", System.Globalization.CultureInfo.InvariantCulture));
            })));
            
            // Number.prototype.toPrecision(precision)
            _numberPrototype.Set("toPrecision", FenValue.FromFunction(new FenFunction("toPrecision", (args, thisVal) =>
            {
                var num = thisVal.ToNumber();
                if (args.Length == 0) return FenValue.FromString(num.ToString(System.Globalization.CultureInfo.InvariantCulture));
                int precision = Math.Max(1, Math.Min(100, (int)args[0].ToNumber()));
                return FenValue.FromString(num.ToString($"G{precision}", System.Globalization.CultureInfo.InvariantCulture));
            })));
            
            // Number.prototype.toExponential(fractionDigits)
            _numberPrototype.Set("toExponential", FenValue.FromFunction(new FenFunction("toExponential", (args, thisVal) =>
            {
                var num = thisVal.ToNumber();
                if (args.Length == 0) return FenValue.FromString(num.ToString("E", System.Globalization.CultureInfo.InvariantCulture).ToLower());
                int digits = Math.Max(0, Math.Min(100, (int)args[0].ToNumber()));
                return FenValue.FromString(num.ToString($"E{digits}", System.Globalization.CultureInfo.InvariantCulture).ToLower());
            })));
            
            // Number.prototype.toString(radix)
            _numberPrototype.Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) =>
            {
                var num = thisVal.ToNumber();
                int radix = args.Length > 0 ? (int)args[0].ToNumber() : 10;
                if (radix < 2 || radix > 36) return FenValue.FromString(num.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (radix == 10) return FenValue.FromString(num.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (double.IsNaN(num)) return FenValue.FromString("NaN");
                if (double.IsPositiveInfinity(num)) return FenValue.FromString("Infinity");
                if (double.IsNegativeInfinity(num)) return FenValue.FromString("-Infinity");
                try
                {
                    long intPart = (long)num;
                    return FenValue.FromString(Convert.ToString(intPart, radix));
                }
                catch { return FenValue.FromString(num.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            })));
            
            // Number.prototype.valueOf()
            _numberPrototype.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) =>
            {
                return FenValue.FromNumber(thisVal.ToNumber());
            })));
            
            // Number.prototype.toLocaleString()
            _numberPrototype.Set("toLocaleString", FenValue.FromFunction(new FenFunction("toLocaleString", (args, thisVal) =>
            {
                var num = thisVal.ToNumber();
                return FenValue.FromString(num.ToString("N", System.Globalization.CultureInfo.CurrentCulture));
            })));
            
            return _numberPrototype;
        }

        private IValue EvalRegexLiteral(RegexLiteral node, FenEnvironment env)
        {
            var options = RegexOptions.None;
            if (node.Flags.Contains("i")) options |= RegexOptions.IgnoreCase;
            if (node.Flags.Contains("m")) options |= RegexOptions.Multiline;
            
            try
            {
                var regex = new Regex(node.Pattern, options);
                var obj = new FenObject();
                obj.NativeObject = regex;
                obj.Set("source", FenValue.FromString(node.Pattern));
                obj.Set("flags", FenValue.FromString(node.Flags));
                obj.Set("lastIndex", FenValue.FromNumber(0));
                
                return FenValue.FromObject(obj);
            }
            catch (Exception ex)
            {
                return FenValue.FromError($"Invalid regular expression: {ex.Message}");
            }
        }
    }

    // ES6+ Interpreter Extensions - partial class
    public partial class Interpreter
    {
        // ES6+ Optional chaining: obj?.prop, obj?.[key], obj?.()
        private FenValue EvalOptionalChainExpression(OptionalChainExpression expr, FenEnvironment env, IExecutionContext context)
        {
            var objVal = Eval(expr.Object, env, context);
            if (IsError(objVal)) return objVal;
            
            // Short-circuit: if null or undefined, return undefined
            if (objVal == null || objVal.IsUndefined)
            {
                return FenValue.Undefined;
            }
            
            if (expr.IsCall)
            {
                // obj?.() - optional call
                if (!objVal.IsFunction)
                {
                    return FenValue.Undefined;
                }
                var args = EvalExpressionsWithSpread(expr.Arguments, env, context);
                return ApplyFunction(objVal, args, context, FenValue.Undefined);
            }
            else if (expr.IsComputed)
            {
                // obj?.[key]
                var keyVal = Eval(expr.Property, env, context);
                if (IsError(keyVal)) return keyVal;
                
                if (objVal.IsObject)
                {
                    return objVal.AsObject()?.Get(keyVal.ToString(), context) ?? FenValue.Undefined;
                }
                return FenValue.Undefined;
            }
            else
            {
                // obj?.prop
                if (objVal.IsObject)
                {
                    return objVal.AsObject()?.Get(expr.PropertyName, context) ?? FenValue.Undefined;
                }
                return FenValue.Undefined;
            }
        }

        // ES6+ Nullish coalescing: a ?? b
        private FenValue EvalNullishCoalescingExpression(NullishCoalescingExpression expr, FenEnvironment env, IExecutionContext context)
        {
            var left = Eval(expr.Left, env, context);
            if (IsError(left)) return left;
            
            if (left == null || left.IsUndefined)
            {
                return Eval(expr.Right, env, context);
            }
            return left;
        }



        // ES6+ Exponentiation: a ** b
        private FenValue EvalExponentiationExpression(ExponentiationExpression expr, FenEnvironment env, IExecutionContext context)
        {
            var left = Eval(expr.Left, env, context);
            if (IsError(left)) return left;
            var right = Eval(expr.Right, env, context);
            if (IsError(right)) return right;
            
            return FenValue.FromNumber(Math.Pow(left.AsNumber(), right.AsNumber()));
        }

        // ES6+ Bitwise NOT: ~x
        private FenValue EvalBitwiseNotExpression(BitwiseNotExpression expr, FenEnvironment env, IExecutionContext context)
        {
            var val = Eval(expr.Operand, env, context);
            if (IsError(val)) return val;
            return FenValue.FromNumber(~(int)val.AsNumber());
        }

        // ES6+ BigInt literal: 123n
        private FenValue EvalBigIntLiteral(BigIntLiteral node)
        {
            // Simplified: treat as number for now until BigInt is fully implemented
            try { return FenValue.FromNumber(Convert.ToDouble(node.Value)); } catch { return FenValue.FromNumber(0); }
        }

        // ES6+ Compound assignment: a += b, a **= b, etc.
        private FenValue EvalCompoundAssignmentExpression(CompoundAssignmentExpression expr, FenEnvironment env, IExecutionContext context)
        {
            // CallExpression as LHS: evaluate the call, then throw ReferenceError
            if (expr.Left is CallExpression)
            {
                Eval(expr.Left, env, context);
                return FenValue.FromError("ReferenceError: Invalid left-hand side in assignment");
            }

            var leftVal = Eval(expr.Left, env, context);
            if (IsError(leftVal)) return leftVal;
            var rightVal = Eval(expr.Right, env, context);
            if (IsError(rightVal)) return rightVal;
            
            FenValue result;
            switch (expr.Operator)
            {
                case "+=":
                    if (leftVal.IsString || rightVal.IsString)
                        result = FenValue.FromString(leftVal.AsString() + rightVal.AsString());
                    else
                        result = FenValue.FromNumber(leftVal.AsNumber() + rightVal.AsNumber());
                    break;
                case "-=":
                    result = FenValue.FromNumber(leftVal.AsNumber() - rightVal.AsNumber());
                    break;
                case "*=":
                    result = FenValue.FromNumber(leftVal.AsNumber() * rightVal.AsNumber());
                    break;
                case "/=":
                    result = FenValue.FromNumber(leftVal.AsNumber() / rightVal.AsNumber());
                    break;
                case "%=":
                    result = FenValue.FromNumber(leftVal.AsNumber() % rightVal.AsNumber());
                    break;
                case "**=":
                    result = FenValue.FromNumber(Math.Pow(leftVal.AsNumber(), rightVal.AsNumber()));
                    break;
                case "<<=":
                    result = FenValue.FromNumber((double)((int)leftVal.AsNumber() << (int)rightVal.AsNumber()));
                    break;
                case ">>=":
                    result = FenValue.FromNumber((double)((int)leftVal.AsNumber() >> (int)rightVal.AsNumber()));
                    break;
                case ">>>=":
                    result = FenValue.FromNumber((double)((uint)(int)leftVal.AsNumber() >> (int)rightVal.AsNumber()));
                    break;
                case "&=":
                    result = FenValue.FromNumber((double)((int)leftVal.AsNumber() & (int)rightVal.AsNumber()));
                    break;
                case "|=":
                    result = FenValue.FromNumber((double)((int)leftVal.AsNumber() | (int)rightVal.AsNumber()));
                    break;
                case "^=":
                    result = FenValue.FromNumber((double)((int)leftVal.AsNumber() ^ (int)rightVal.AsNumber()));
                    break;
                default:
                    result = FenValue.Undefined;
                    break;
            }
            
            // Assign result back
            if (expr.Left is Identifier ident)
            {
                env.Update(ident.Value, result);
            }
            else if (expr.Left is MemberExpression member)
            {
                var obj = Eval(member.Object, env, context);
                if (obj.IsObject)
                {
                    obj.AsObject()?.Set(member.Property, result, context);
                }
            }
            
            return result;
        }

        private FenValue EvalPrefixUpdate(PrefixExpression expr, FenEnvironment env, IExecutionContext context)
        {
            var val = Eval(expr.Right, env, context);
            if (IsError(val)) return val;
            
            double num = val.AsNumber();
            double resultNum = expr.Operator == "++" ? num + 1 : num - 1;
            var result = FenValue.FromNumber(resultNum);
            
            // Assign result back
            if (expr.Right is Identifier ident)
            {
                env.Update(ident.Value, result);
            }
            else if (expr.Right is MemberExpression member)
            {
                var obj = Eval(member.Object, env, context);
                if (obj.IsObject) obj.AsObject()?.Set(member.Property, result, context);
            }
            else if (expr.Right is IndexExpression index)
            {
                var obj = Eval(index.Left, env, context);
                var idx = Eval(index.Index, env, context);
                if (obj.IsObject) obj.AsObject()?.Set(idx.ToString(), result, context);
            }
            
            return result;
        }

        private FenValue EvalPostfixUpdate(Expression operand, string op, FenEnvironment env, IExecutionContext context)
        {
            var originalValue = Eval(operand, env, context);
            if (IsError(originalValue)) return originalValue;
            
            double num = originalValue.AsNumber();
            double resultNum = op == "++" ? num + 1 : num - 1;
            var result = FenValue.FromNumber(resultNum);
            
            // Assign result back
            if (operand is Identifier ident)
            {
                env.Update(ident.Value, result);
            }
            else if (operand is MemberExpression member)
            {
                var obj = Eval(member.Object, env, context);
                if (obj.IsObject) obj.AsObject()?.Set(member.Property, result, context);
            }
            else if (operand is IndexExpression index)
            {
                var obj = Eval(index.Left, env, context);
                var idx = Eval(index.Index, env, context);
                if (obj.IsObject) obj.AsObject()?.Set(idx.ToString(), result, context);
            }
            
            return originalValue; // Return OLD value
        }
        private IValue EvalLogicalAssignment(LogicalAssignmentExpression node, FenEnvironment env, IExecutionContext context)
        {
            // 1. Evaluate Left (LHS)
            // Note: In spec, we evaluate the *reference* first (base + name), but here we largely eval value for condition check
            // However, to assign, we need to know *where* to assign.
            // Simplified approach: Evaluate Left to value to check condition. If assignment needed, re-evaluate target for assignment logic.
            // This mirrors how simple assignment works but with a condition check.
            
            var leftVal = Eval(node.Left, env, context);
            if (IsError(leftVal)) return leftVal;
            
            bool shouldAssign = false;
            
            switch (node.Operator)
            {
                case "||=": 
                    // Assign if Left is Falsy
                    if (!leftVal.ToBoolean()) shouldAssign = true;
                    break;
                    
                case "&&=": 
                    // Assign if Left is Truthy
                    if (leftVal.ToBoolean()) shouldAssign = true;
                    break;
                    
                case "??=": 
                    // Assign if Left is Null or Undefined
                    if (leftVal.Type == JsValueType.Null || leftVal.Type == JsValueType.Undefined) shouldAssign = true;
                    break;
            }
            
            if (!shouldAssign)
            {
                // Short-circuit: return current left value
                return leftVal;
            }
            
            // 2. Evaluate Right (RHS)
            var rightVal = Eval(node.Right, env, context);
            if (IsError(rightVal)) return rightVal;
            
            // 3. Perform Assignment
            // We reuse the logic from AssignmentExpression but manually constructing one or duplicating logic?
            // Duplicating logic is safer to avoid creating dummy AST nodes.
            
            // Handle assignment to identifier (var x = ...)
            if (node.Left is Identifier leftIdent)
            {
                env.Update(leftIdent.Value, rightVal);
                return rightVal;
            }
            
            // Handle assignment to private identifier (this.#field = ...)
            if (node.Left is PrivateIdentifier leftPrivate)
            {
                var thisVal = env.Get("this");
                if (thisVal == null || !thisVal.IsObject)
                {
                    return FenValue.FromError($"Cannot assign to private field '#${leftPrivate.Name}' outside of a class");
                }
                var thisObj = thisVal.AsObject();
                string privateKey = "#" + leftPrivate.Name;
                thisObj.Set(privateKey, rightVal);
                return rightVal;
            }
            
            // Handle assignment to member expression (obj.prop = ...)
            if (node.Left is MemberExpression leftMember)
            {
                var targetObj = Eval(leftMember.Object, env, context);
                if (IsError(targetObj)) return targetObj;
                
                if (targetObj.IsObject)
                {
                    var targetObjVal = targetObj.AsObject();
                    if (targetObjVal != null)
                    {
                        targetObjVal.Set(leftMember.Property, rightVal, context);
                        return rightVal;
                    }
                }
            }
            
            // Handle assignment to index expression (obj[key] = ...)
            if (node.Left is IndexExpression leftIndex)
            {
                var targetObj = Eval(leftIndex.Left, env, context);
                if (IsError(targetObj)) return targetObj;
                
                var indexVal = Eval(leftIndex.Index, env, context);
                if (IsError(indexVal)) return indexVal;
                
                var key = indexVal.ToString();
                
                if (targetObj.IsObject)
                {
                    var targetObjVal = targetObj.AsObject();
                    if (targetObjVal != null)
                    {
                        targetObjVal.Set(key, rightVal, context);
                        return rightVal;
                    }
                }
            }
            
            // Fallback for invalid assignment targets (runtime error in strict mode, or ignore)
            return FenValue.FromError("Invalid left-hand side in logical assignment");
        }

        /// <summary>
        /// Apply decorators to a target (class, method, or property)
        /// Simplified Stage 3 decorator implementation
        /// </summary>
        private FenValue ApplyDecorators(List<Decorator> decorators, FenValue target, IExecutionContext context, FenEnvironment env)
        {
            if (decorators == null || decorators.Count == 0)
                return target;

            // Decorators are applied bottom-to-top (innermost to outermost)
            var result = target;
            for (int i = decorators.Count - 1; i >= 0; i--)
            {
                var decorator = decorators[i];
                var decoratorFunc = Eval(decorator.Expression, env, context);
                
                if (!decoratorFunc.IsFunction)
                {
                    continue; // Skip invalid decorators
                }

                var fn = decoratorFunc.AsFunction();
                if (fn == null) continue;

                // Call decorator with target as argument
                var decoratedResult = fn.Invoke(new FenValue[] { result }, context);
                
                // If decorator returns a value, use it; otherwise keep original
                if (decoratedResult != null && !decoratedResult.IsUndefined)
                {
                    result = ToFenValue(decoratedResult);
                }
            }

            return result;
        }
    }
}
