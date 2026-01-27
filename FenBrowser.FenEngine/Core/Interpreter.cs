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

                case NullLiteral _:
                    return FenValue.Null;

                case UndefinedLiteral _:
                    return FenValue.Undefined;

                case PrefixExpression prefixExpr:
                    if (prefixExpr.Operator == "++" || prefixExpr.Operator == "--")
                    {
                        return EvalPrefixUpdate(prefixExpr, env, context);
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

                case LetStatement letStmt:
                    var valLet = Eval(letStmt.Value, env, context);
                    if (IsError(valLet)) return valLet;

                    if (letStmt.DestructuringPattern != null)
                    {
                        return (FenValue)EvalDestructuringAssignment(letStmt.DestructuringPattern, valLet, env, context);
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
                        env.Update(leftIdent.Value, value);
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
                    return FenValue.Break;

                // Continue statement
                case ContinueStatement continueStmt:
                    return FenValue.Continue;

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
                    return (FenValue)EvalLogicalAssignmentExpression(logicalAssignExpr, env, context);

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

            }

            return FenValue.Undefined;
        }
        
        /// <summary>
        /// Evaluate yield expression - returns a YieldValue for generator iteration
        /// </summary>
        private IValue EvalYieldExpression(YieldExpression yieldExpr, FenEnvironment env, IExecutionContext context)
        {
            var value = yieldExpr.Value != null ? Eval(yieldExpr.Value, env, context) : FenValue.Undefined;
            return FenValue.FromYield(value);
        }

        private IValue EvalArrayLiteral(ArrayLiteral node, FenEnvironment env, IExecutionContext context)
        {
            var elements = EvalExpressionsWithSpread(node.Elements, env, context);
            if (elements.Count == 1 && IsError(elements[0])) return elements[0];
            
            var arrayObj = new FenObject();
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
                
                var result = new FenObject();
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
                
                var deleted = new FenObject();
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
                var result = new FenObject();
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
                var result = new FenObject();
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
                if (obj  == null) return FenValue.FromObject(new FenObject());
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                var depth = args.Length > 0 ? args[0].ToNumber() : 1;
                
                var result = new FenObject();
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
                if (obj  == null || args.Length == 0 || !args[0].IsFunction) return FenValue.FromObject(new FenObject());
                
                var callback = args[0].AsFunction();
                var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
                var lenVal = obj.Get("length", null);
                var len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                
                var result = new FenObject();
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
                var result = new FenObject();
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
                var val = Eval(pair.Value, env, null);
                if (IsError(val)) return val;
                obj.Set(pair.Key, val, null);
            }
            return FenValue.FromObject(obj);
        }

        private IValue EvalProgram(Program program, FenEnvironment env, IExecutionContext context)
        {
            FenValue result = FenValue.Null;

            foreach (var stmt in program.Statements)
            {
                result = Eval(stmt, env, context);

                if (result.Type == JsValueType.ReturnValue)
                {
                    return result.GetReturnValue();
                }
                
                if (result.Type == JsValueType.Error)
                {
                    return result;
                }
            }

            return result;
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
                case "typeof":
                    return EvalTypeofExpression(right);
                case "void":
                    return FenValue.Undefined;
                case "delete":
                    return FenValue.FromBoolean(true);  // Simplified
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
                default:
                    return FenValue.FromString("object");
            }
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

            // in operator
            if (op == "in")
            {
                if (right.IsObject)
                {
                    var obj = right.AsObject();
                    if (obj != null)
                    {
                        var val = obj.Get(left.ToString());
                        return FenValue.FromBoolean(val != null && !val.IsUndefined);
                    }
                }
                return FenValue.FromBoolean(false);
            }

            if (op == "+")
            {
                if (left.Type == JsValueType.String || right.Type == JsValueType.String)
                {
                    return FenValue.FromString(left.ToString() + right.ToString());
                }
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
            {
                return FenValue.FromBoolean(left.LooseEquals(right));
            }
            if (op == "!=")
            {
                return FenValue.FromBoolean(!left.LooseEquals(right));
            }
            if (left.Type != right.Type)
            {
                return FenValue.FromError($"type mismatch: {left.Type} {op} {right.Type}");
            }
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
                
                // Handle user-defined functions (Interpreter fallback)
                context.PushCallFrame(function.Name ?? "anonymous");
                var extendedEnv = ExtendFunctionEnv(function, args, context, thisContext);
                
                // DevTools CallStack Hook
                DevToolsCore.Instance.PushCallFrame(function.Name ?? "anonymous", context.CurrentUrl, -1, extendedEnv);

                IValue evaluated;
                try
                {
                    evaluated = Eval(function.Body, extendedEnv, context);
                }
                finally
                {
                    DevToolsCore.Instance.PopCallFrame();
                    context.PopCallFrame();
                }
                
                return UnwrapReturnValue(ToFenValue(evaluated));
            }
            
            return FenValue.FromError($"not a function: {fn.Type}");
        }

        private FenEnvironment ExtendFunctionEnv(FenFunction fn, List<FenValue> args, IExecutionContext context, FenValue thisContext = default)
        {
            var env = new FenEnvironment(fn.Env);
            if (fn.LocalMap != null) env.InitializeFastStore(fn.LocalMap.Count);
            
            // Bind 'this'
            if (thisContext != null)
            {
                env.Set("this", thisContext);
            }
            else
            {
                // Default 'this' to global or undefined?
                // For now, undefined or maybe the environment itself?
                // In strict mode it's undefined. In non-strict it's global.
                // Let's set it to undefined if not provided.
                env.Set("this", FenValue.Undefined);
            }
            if (fn.LocalMap != null && fn.LocalMap.TryGetValue("this", out int thisIdx)) env.SetFast(thisIdx, env.Get("this"));

            // Create 'arguments' object for non-arrow functions
            // Arrow functions do NOT have their own arguments object
            if (!fn.IsArrowFunction)
            {
                var argumentsObj = new FenObject();
                for (int i = 0; i < args.Count; i++)
                {
                    argumentsObj.Set(i.ToString(), args[i], context);
                }
                argumentsObj.Set("length", FenValue.FromNumber(args.Count), context);
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

                if (i < args.Count && !args[i].IsUndefined)
                {
                    env.Set(param.Value, args[i]);
                    if (fn.LocalMap != null && fn.LocalMap.TryGetValue(param.Value, out int pIdx)) env.SetFast(pIdx, args[i]);
                }
                else if (param.DefaultValue != null)
                {
                    // Evaluate default value in the new env (so it can see previous params)
                    var defaultVal = Eval(param.DefaultValue, env, context);
                    env.Set(param.Value, defaultVal);
                }
                else
                {
                    env.Set(param.Value, FenValue.Undefined);
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

        // Evaluate tagged template literals: tag`Hello ${name}!`
        // The tag function receives (stringsArray, ...interpolatedValues)
        private IValue EvalTaggedTemplate(TaggedTemplateExpression taggedExpr, FenEnvironment env, IExecutionContext context)
        {
            // Evaluate the tag function
            var tagFn = Eval(taggedExpr.Tag, env, context);
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
            var result = Eval(fn.Body, newEnv, context);
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

            // Catch block handling
            if (result.Type == JsValueType.Error && ts.CatchBlock != null)
            {
                var catchEnv = new FenEnvironment(env);
                if (ts.CatchParameter != null)
                {
                    // Create error object with message and name properties
                    var errorObj = new FenObject();
                    errorObj.Set("message", FenValue.FromString(result.AsError()));
                    errorObj.Set("name", FenValue.FromString("Error"));
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

        private IValue EvalWhileStatement(WhileStatement ws, FenEnvironment env, IExecutionContext context)
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
                    if (result.Type == JsValueType.Break) return FenValue.Null;
                    if (result.Type == JsValueType.Continue) continue;
                }
            }

            return result;
        }

        private IValue EvalForStatement(ForStatement fs, FenEnvironment env, IExecutionContext context)
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
                    if (result.Type == JsValueType.Break) return FenValue.Null;
                    // Continue just falls through to update
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
        private IValue EvalForInStatement(ForInStatement fs, FenEnvironment env, IExecutionContext context)
        {
            var loopEnv = new FenEnvironment(env);
            var objVal = Eval(fs.Object, env, context);
            if (IsError(objVal)) return objVal;

            FenValue result = FenValue.Null;

            // If object, iterate over its keys
            if (objVal.IsObject)
            {
                var obj = objVal.AsObject();
                if (obj != null)
                {
                    var keys = obj.Keys();
                    foreach (var key in keys)
                    {
                        // Set the loop variable to current key
                        loopEnv.Set(fs.Variable.Value, FenValue.FromString(key));
                        
                        result = Eval(fs.Body, loopEnv, context);
                        
                        if (!result.IsUndefined && !result.IsNull)
                        {
                            if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                            if (result.Type == JsValueType.Break) return FenValue.Null;
                            if (result.Type == JsValueType.Continue) continue;
                        }
                    }
                }
            }

            return result;
        }

        // Evaluate for-of: for (x of iterable) { ... }
        // Implements the full iterator protocol: obj[Symbol.iterator]().next()
        private IValue EvalForOfStatement(ForOfStatement fs, FenEnvironment env, IExecutionContext context)
        {
            var loopEnv = new FenEnvironment(env);
            var iterableVal = Eval(fs.Iterable, env, context);
            if (IsError(iterableVal)) return iterableVal;

            FenValue result = FenValue.Null;

            // Handle strings first (iterate over characters)
            if (iterableVal.IsString)
            {
                string str = iterableVal.ToString();
                foreach (char c in str)
                {
                    loopEnv.Set(fs.Variable.Value, FenValue.FromString(c.ToString()));
                    
                    result = Eval(fs.Body, loopEnv, context);
                    
                    if (!result.IsUndefined && !result.IsNull)
                    {
                        if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                        if (result.Type == JsValueType.Break) return FenValue.Null;
                        if (result.Type == JsValueType.Continue) continue;
                    }
                }
                return result;
            }

            // Handle objects with iterator protocol
            if (iterableVal.IsObject)
            {
                var obj = iterableVal.AsObject();
                if (obj != null)
                {
                    // Check for Symbol.iterator method
                    // The symbol is stored as an object with __symbolId__ = -1
                    IValue iteratorMethod = null;
                    
                    // Try to get the iterator method via Symbol.iterator
                    // Symbol.iterator is stored with key "[Symbol.iterator]" or we need to look it up
                    var symbolIteratorMethod = obj.Get("[Symbol.iterator]");
                    if (symbolIteratorMethod != null && symbolIteratorMethod.IsFunction)
                    {
                        iteratorMethod = symbolIteratorMethod;
                    }
                    
                    // Also check for a method keyed by Symbol.iterator object reference
                    // Maps, Sets, and custom iterables may use this
                    if (iteratorMethod  == null)
                    {
                        // Check for __iterator__ (some implementations use this)
                        var altIterator = obj.Get("__iterator__");
                        if (altIterator != null && altIterator.IsFunction)
                        {
                            iteratorMethod = altIterator;
                        }
                    }
                    
                    // If we have an iterator method, use the full protocol
                    if (iteratorMethod != null && iteratorMethod.IsFunction)
                    {
                        // Call the iterator method to get the iterator object
                        var iterator = ApplyFunction(FenValue.FromFunction(iteratorMethod.AsFunction()), new List<FenValue>(), context, ToFenValue(iterableVal));
                        if (IsError(iterator)) return iterator;
                        
                        if (iterator.IsObject)
                        {
                            var iterObj = iterator.AsObject();
                            var nextMethod = iterObj?.Get("next");
                            
                            if (nextMethod != null && nextMethod.Value.IsFunction)
                            {
                                // Iterate using next() until done is true
                                while (true)
                                {
                                    var iterResult = ApplyFunction(FenValue.FromFunction(nextMethod.Value.AsFunction()), new List<FenValue>(), context, iterator);
                                    if (IsError(iterResult)) return iterResult;
                                    
                                    if (iterResult.IsObject)
                                    {
                                        var resultObj = iterResult.AsObject();
                                        var doneVal = resultObj?.Get("done");
                                        var valueVal = resultObj?.Get("value");
                                        
                                        if (doneVal != null && IsTruthy(doneVal.Value))
                                        {
                                            break; // Iteration complete
                                        }
                                        
                                        loopEnv.Set(fs.Variable.Value, valueVal ?? FenValue.Undefined);
                                        result = Eval(fs.Body, loopEnv, context);
                                        
                                        if (!result.IsUndefined && !result.IsNull)
                                        {
                                            if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                                            if (result.Type == JsValueType.Break) return FenValue.Null;
                                            if (result.Type == JsValueType.Continue) continue;
                                        }
                                    }
                                    else
                                    {
                                        break; // Invalid iterator result
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
                                loopEnv.Set(fs.Variable.Value, val );

                                result = Eval(fs.Body, loopEnv, context);

                                if (!result.IsUndefined && !result.IsNull)
                                {
                                    if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                                    if (result.Type == JsValueType.Break) return FenValue.Null;
                                    if (result.Type == JsValueType.Continue) continue;
                                }
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
        private IValue EvalDoWhileStatement(DoWhileStatement ds, FenEnvironment env, IExecutionContext context)
        {
            FenValue result = FenValue.Null;
            var loopEnv = new FenEnvironment(env);

            do
            {
                result = Eval(ds.Body, loopEnv, context);

                if (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error) return result;
                if (result.Type == JsValueType.Break) return FenValue.Null;
                if (result.Type == JsValueType.Continue) 
                {
                    // Continue goes to condition check
                }

                var condition = Eval(ds.Condition, loopEnv, context);
                if (IsError(condition)) return condition;
                if (!IsTruthy(condition)) break;

            } while (true);

            return result;
        }

        private IValue EvalDestructuringAssignment(Expression pattern, FenValue value, FenEnvironment env, IExecutionContext context)
        {
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
                            var val = obj.Get(i.ToString()) ;

                            if (element is Identifier ident)
                            {
                                env.Update(ident.Value, val);
                            }
                            else if (element is AssignmentExpression assign)
                            {
                                // Default value: [a = 1]
                                if (assign.Left is Identifier leftIdent)
                                {
                                    if (val.IsUndefined)
                                    {
                                        val = Eval(assign.Right, env, context);
                                    }
                                    env.Update(leftIdent.Value, val);
                                }
                            }
                            else if (element is SpreadElement spread)
                            {
                                // Rest: [...rest]
                                // Collect remaining elements
                                if (spread.Argument is Identifier restIdent)
                                {
                                    var restArray = new FenObject();
                                    int restIndex = 0;
                                    
                                    // We need to know the length of the source array
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
                                    env.Update(restIdent.Value, FenValue.FromObject(restArray));
                                }
                                // Spread must be last, so break
                                break;
                            }
                            else if (element is ArrayLiteral || element is ObjectLiteral)
                            {
                                // Nested destructuring
                                EvalDestructuringAssignment(element, val, env, context);
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
                        foreach (var pair in objectPattern.Pairs)
                        {
                            var key = pair.Key;
                            var target = pair.Value;
                            var val = obj.Get(key) ;

                            if (target is Identifier ident)
                            {
                                env.Update(ident.Value, val);
                            }
                            else if (target is AssignmentExpression assign)
                            {
                                // Default value: {a = 1}
                                // In ParseObjectLiteral, we stored this as AssignmentExpression
                                // Left is the Identifier (target), Right is default value
                                
                                if (assign.Left is Identifier leftIdent)
                                {
                                    if (val.IsUndefined)
                                    {
                                        val = Eval(assign.Right, env, context);
                                    }
                                    env.Update(leftIdent.Value, val);
                                }
                            }
                            else if (target is ArrayLiteral || target is ObjectLiteral)
                            {
                                // Nested destructuring: {a: [x, y]}
                                EvalDestructuringAssignment(target, val, env, context);
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

            // Store class field definitions on the constructor for initialization during `new`
            // We store both regular and private field definitions
            var fieldDefinitions = new List<(string name, bool isPrivate, bool isStatic, Expression initializer)>();
            foreach (var prop in stmt.Properties)
            {
                string fieldName = prop.IsPrivate ? "#" + prop.Key.Value : prop.Key.Value;
                fieldDefinitions.Add((fieldName, prop.IsPrivate, prop.Static, prop.Value));
            }
            
            // Store the field definitions on the constructor
            constructor.FieldDefinitions = fieldDefinitions;

            // Link constructor and prototype
            constructor.Prototype = prototype;
            
            // For now, let's bind the class name to the constructor function
            env.Set(stmt.Name.Value, FenValue.FromFunction(constructor));
            
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
            if (context.ModuleLoader  == null) return FenValue.Undefined;

            string path = context.ModuleLoader.Resolve(stmt.Source, ""); // Referrer unknown for now
            IObject moduleExports = context.ModuleLoader.LoadModule(path);

            // Bind imports
            foreach (var specifier in stmt.Specifiers)
            {
                // Assuming default import for now or named imports
                // Simplified: import { x } from ...
                // We need ImportSpecifier AST node details
                // But for now, just bind everything if it matches?
                
                // If specifier has Local and Imported names
                // env.Set(specifier.Local.Value, moduleExports.Get(specifier.Imported.Value));
                
                // Since AST might be simplified, let's assume we just load the module for side effects
                // if no specifiers.
            }
            
            return FenValue.Undefined;
        }

        private IValue EvalExportDeclaration(ExportDeclaration stmt, FenEnvironment env, IExecutionContext context)
        {
            if (stmt.Declaration != null)
            {
                var val = Eval(stmt.Declaration, env, context);
                
                // If declaration is a LetStatement (var/let/const)
                // We need to find the name.
                // But Eval returns the value, not the name.
                
                // We need to inspect stmt.Declaration
                if (stmt.Declaration is LetStatement letStmt)
                {
                    Exports[letStmt.Name.Value] = env.Get(letStmt.Name.Value);
                }
                return val;
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
            // Evaluate the argument (the promise or value)
            var value = Eval(node.Argument, env, context);
            if (IsError(value)) return value;

            // In a real engine, we would suspend execution here if value is a Promise
            // For now, we assume synchronous execution or that the promise resolves immediately
            // This is a placeholder for full async support
            
            // If value is a Promise object (which we don't have yet), we'd unwrap it
            // For now, just return the value
            return value;
        }

        private FenObject GetStringPrototype()
        {
            if (_stringPrototype != null) return _stringPrototype;

            _stringPrototype = new FenObject();
            
            // String.prototype.match(regexp)
            _stringPrototype.Set("match", FenValue.FromFunction(new FenFunction("match", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                var regexVal = args.Length > 0 ? args[0] : FenValue.Undefined;
                
                Regex regex;
                if (regexVal.IsObject && regexVal.AsObject() is FenObject fenObj && fenObj.NativeObject is Regex r)
                {
                    regex = r;
                }
                else
                {
                    regex = new Regex(regexVal.ToString());
                }
                
                var match = regex.Match(str);
                if (!match.Success) return FenValue.Null;
                
                var arr = new FenObject();
                arr.Set("0", FenValue.FromString(match.Value));
                for (int i = 1; i < match.Groups.Count; i++)
                {
                    arr.Set(i.ToString(), FenValue.FromString(match.Groups[i].Value));
                }
                arr.Set("index", FenValue.FromNumber(match.Index));
                arr.Set("input", FenValue.FromString(str));
                arr.Set("length", FenValue.FromNumber(match.Groups.Count > 0 ? match.Groups.Count : 1));
                
                return FenValue.FromObject(arr);
            })));

            // String.prototype.search(regexp)
            _stringPrototype.Set("search", FenValue.FromFunction(new FenFunction("search", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                var regexVal = args.Length > 0 ? args[0] : FenValue.Undefined;
                
                Regex regex;
                if (regexVal.IsObject && regexVal.AsObject() is FenObject fenObj && fenObj.NativeObject is Regex r)
                {
                    regex = r;
                }
                else
                {
                    regex = new Regex(regexVal.ToString());
                }
                
                var match = regex.Match(str);
                return FenValue.FromNumber(match.Success ? match.Index : -1);
            })));

            // String.prototype.replace(searchValue, replaceValue)
            _stringPrototype.Set("replace", FenValue.FromFunction(new FenFunction("replace", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromString(str);
                
                var searchVal = args[0];
                var replaceVal = args.Length > 1 ? args[1] : FenValue.Undefined;
                string replacement = replaceVal.ToString();
                
                if (searchVal.IsObject && searchVal.AsObject() is FenObject fenObj && fenObj.NativeObject is Regex regex)
                {
                    return FenValue.FromString(regex.Replace(str, replacement));
                }
                else
                {
                    string searchStr = searchVal.ToString();
                    int idx = str.IndexOf(searchStr);
                    if (idx >= 0)
                    {
                        return FenValue.FromString(str.Substring(0, idx) + replacement + str.Substring(idx + searchStr.Length));
                    }
                    return FenValue.FromString(str);
                }
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

            // String.prototype.trimStart() / trimLeft()
            _stringPrototype.Set("trimStart", FenValue.FromFunction(new FenFunction("trimStart", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().TrimStart());
            })));
            _stringPrototype.Set("trimLeft", FenValue.FromFunction(new FenFunction("trimLeft", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().TrimStart());
            })));

            // String.prototype.trimEnd() / trimRight()
            _stringPrototype.Set("trimEnd", FenValue.FromFunction(new FenFunction("trimEnd", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().TrimEnd());
            })));
            _stringPrototype.Set("trimRight", FenValue.FromFunction(new FenFunction("trimRight", (args, thisVal) =>
            {
                return FenValue.FromString(thisVal.ToString().TrimEnd());
            })));

            // String.prototype.charAt(index)
            _stringPrototype.Set("charAt", FenValue.FromFunction(new FenFunction("charAt", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int idx = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (idx < 0 || idx >= str.Length) return FenValue.FromString("");
                return FenValue.FromString(str[idx].ToString());
            })));

            // String.prototype.charCodeAt(index)
            _stringPrototype.Set("charCodeAt", FenValue.FromFunction(new FenFunction("charCodeAt", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int idx = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (idx < 0 || idx >= str.Length) return FenValue.FromNumber(double.NaN);
                return FenValue.FromNumber((int)str[idx]);
            })));

            // String.prototype.substring(start, end)
            _stringPrototype.Set("substring", FenValue.FromFunction(new FenFunction("substring", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int start = args.Length > 0 ? Math.Max(0, Math.Min((int)args[0].ToNumber(), str.Length)) : 0;
                int end = args.Length > 1 ? Math.Max(0, Math.Min((int)args[1].ToNumber(), str.Length)) : str.Length;
                if (start > end) { int temp = start; start = end; end = temp; }
                return FenValue.FromString(str.Substring(start, end - start));
            })));

            // String.prototype.slice(start, end)
            _stringPrototype.Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int len = str.Length;
                int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int end = args.Length > 1 ? (int)args[1].ToNumber() : len;
                if (start < 0) start = Math.Max(len + start, 0);
                if (end < 0) end = Math.Max(len + end, 0);
                start = Math.Min(start, len);
                end = Math.Min(end, len);
                if (start >= end) return FenValue.FromString("");
                return FenValue.FromString(str.Substring(start, end - start));
            })));

            // String.prototype.split(separator, limit)
            _stringPrototype.Set("split", FenValue.FromFunction(new FenFunction("split", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                var arr = new FenObject();
                
                if (args.Length == 0 || args[0].IsUndefined)
                {
                    arr.Set("0", FenValue.FromString(str));
                    arr.Set("length", FenValue.FromNumber(1));
                    return FenValue.FromObject(arr);
                }
                
                var separator = args[0].ToString();
                int limit = args.Length > 1 && !args[1].IsUndefined ? (int)args[1].ToNumber() : int.MaxValue;
                
                string[] parts;
                if (string.IsNullOrEmpty(separator))
                {
                    parts = str.Select(c => c.ToString()).ToArray();
                }
                else
                {
                    parts = str.Split(new[] { separator }, StringSplitOptions.None);
                }
                
                int count = Math.Min(parts.Length, limit);
                for (int i = 0; i < count; i++)
                {
                    arr.Set(i.ToString(), FenValue.FromString(parts[i]));
                }
                arr.Set("length", FenValue.FromNumber(count));
                return FenValue.FromObject(arr);
            })));

            // String.prototype.indexOf(searchValue, fromIndex)
            _stringPrototype.Set("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromNumber(-1);
                var search = args[0].ToString();
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                from = Math.Max(0, Math.Min(from, str.Length));
                return FenValue.FromNumber(str.IndexOf(search, from, StringComparison.Ordinal));
            })));

            // String.prototype.lastIndexOf(searchValue, fromIndex)
            _stringPrototype.Set("lastIndexOf", FenValue.FromFunction(new FenFunction("lastIndexOf", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromNumber(-1);
                var search = args[0].ToString();
                int from = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                from = Math.Max(0, Math.Min(from, str.Length));
                if (from == 0 && str.Length > 0) return FenValue.FromNumber(str.StartsWith(search) ? 0 : -1);
                return FenValue.FromNumber(str.LastIndexOf(search, from, StringComparison.Ordinal));
            })));

            // String.prototype.includes(searchString, position)
            _stringPrototype.Set("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var search = args[0].ToString();
                int pos = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                pos = Math.Max(0, Math.Min(pos, str.Length));
                return FenValue.FromBoolean(str.IndexOf(search, pos, StringComparison.Ordinal) >= 0);
            })));

            // String.prototype.startsWith(searchString, position)
            _stringPrototype.Set("startsWith", FenValue.FromFunction(new FenFunction("startsWith", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var search = args[0].ToString();
                int pos = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                pos = Math.Max(0, Math.Min(pos, str.Length));
                return FenValue.FromBoolean(str.Substring(pos).StartsWith(search, StringComparison.Ordinal));
            })));

            // String.prototype.endsWith(searchString, length)
            _stringPrototype.Set("endsWith", FenValue.FromFunction(new FenFunction("endsWith", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromBoolean(false);
                var search = args[0].ToString();
                int len = args.Length > 1 ? (int)args[1].ToNumber() : str.Length;
                len = Math.Max(0, Math.Min(len, str.Length));
                return FenValue.FromBoolean(str.Substring(0, len).EndsWith(search, StringComparison.Ordinal));
            })));

            // String.prototype.repeat(count)
            _stringPrototype.Set("repeat", FenValue.FromFunction(new FenFunction("repeat", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int count = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (count < 0) return FenValue.FromError("Invalid count value");
                if (count == 0 || str.Length == 0) return FenValue.FromString("");
                return FenValue.FromString(string.Concat(Enumerable.Repeat(str, count)));
            })));

            // String.prototype.padStart(targetLength, padString)
            _stringPrototype.Set("padStart", FenValue.FromFunction(new FenFunction("padStart", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int targetLen = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (targetLen <= str.Length) return FenValue.FromString(str);
                var padStr = args.Length > 1 ? args[1].ToString() : " ";
                if (string.IsNullOrEmpty(padStr)) return FenValue.FromString(str);
                
                int padLen = targetLen - str.Length;
                var sb = new StringBuilder();
                while (sb.Length < padLen) sb.Append(padStr);
                return FenValue.FromString(sb.ToString().Substring(0, padLen) + str);
            })));

            // String.prototype.padEnd(targetLength, padString)
            _stringPrototype.Set("padEnd", FenValue.FromFunction(new FenFunction("padEnd", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int targetLen = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (targetLen <= str.Length) return FenValue.FromString(str);
                var padStr = args.Length > 1 ? args[1].ToString() : " ";
                if (string.IsNullOrEmpty(padStr)) return FenValue.FromString(str);
                
                int padLen = targetLen - str.Length;
                var sb = new StringBuilder(str);
                while (sb.Length < targetLen) sb.Append(padStr);
                return FenValue.FromString(sb.ToString().Substring(0, targetLen));
            })));

            // String.prototype.padStart(targetLength, padString)
            _stringPrototype.Set("padStart", FenValue.FromFunction(new FenFunction("padStart", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int targetLen = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (targetLen <= str.Length) return FenValue.FromString(str);
                var padStr = args.Length > 1 ? args[1].ToString() : " ";
                if (string.IsNullOrEmpty(padStr)) return FenValue.FromString(str);
                int padLen = targetLen - str.Length;
                var sb = new StringBuilder();
                while (sb.Length < padLen) sb.Append(padStr);
                return FenValue.FromString(sb.ToString().Substring(0, padLen) + str);
            })));

            // String.prototype.padEnd(targetLength, padString)
            _stringPrototype.Set("padEnd", FenValue.FromFunction(new FenFunction("padEnd", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                int targetLen = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                if (targetLen <= str.Length) return FenValue.FromString(str);
                var padStr = args.Length > 1 ? args[1].ToString() : " ";
                if (string.IsNullOrEmpty(padStr)) return FenValue.FromString(str);
                int padLen = targetLen - str.Length;
                var sb = new StringBuilder(str);
                while (sb.Length < targetLen) sb.Append(padStr);
                return FenValue.FromString(sb.ToString().Substring(0, targetLen));
            })));

            // String.prototype.concat(...strings)
            _stringPrototype.Set("concat", FenValue.FromFunction(new FenFunction("concat", (args, thisVal) =>
            {
                var sb = new StringBuilder(thisVal.ToString());
                foreach (var arg in args)
                {
                    sb.Append(arg.ToString());
                }
                return FenValue.FromString(sb.ToString());
            })));

            // String.prototype.replaceAll(searchValue, replaceValue)
            _stringPrototype.Set("replaceAll", FenValue.FromFunction(new FenFunction("replaceAll", (args, thisVal) =>
            {
                var str = thisVal.ToString();
                if (args.Length == 0) return FenValue.FromString(str);
                var searchVal = args[0];
                var replaceVal = args.Length > 1 ? args[1].ToString() : "undefined";
                
                if (searchVal.IsObject && searchVal.AsObject() is FenObject fenObj && fenObj.NativeObject is Regex regex)
                {
                    return FenValue.FromString(regex.Replace(str, replaceVal));
                }
                else
                {
                    return FenValue.FromString(str.Replace(searchVal.ToString(), replaceVal));
                }
            })));

            return _stringPrototype;
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

        // ES6+ Logical assignment: a ||= b, a &&= b, a ??= b
        private FenValue EvalLogicalAssignmentExpression(LogicalAssignmentExpression expr, FenEnvironment env, IExecutionContext context)
        {
            var left = Eval(expr.Left, env, context);
            if (IsError(left)) return left;
            
            bool shouldAssign = false;
            switch (expr.Operator)
            {
                case "||=": shouldAssign = !left.ToBoolean(); break;
                case "&&=": shouldAssign = left.ToBoolean(); break;
                case "??=": shouldAssign = left == null || left.IsUndefined; break;
            }
            
            if (shouldAssign)
            {
                var right = Eval(expr.Right, env, context);
                if (IsError(right)) return right;
                
                // Assign back
                if (expr.Left is Identifier ident)
                {
                    env.Update(ident.Value, right);
                }
                else if (expr.Left is MemberExpression member)
                {
                    var target = Eval(member.Object, env, context);
                    if (target.IsObject) target.AsObject()?.Set(member.Property, right, context);
                }
                return right;
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
    }
}

