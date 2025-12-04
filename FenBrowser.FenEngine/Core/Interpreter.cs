using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using System.Text.RegularExpressions;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.FenEngine.Core
{
    public class Interpreter
    {
        private FenObject _stringPrototype;

        public IValue Eval(AstNode node, FenEnvironment env, IExecutionContext context)
        {
            if (node == null) return FenValue.Null;

            switch (node)
            {
                case Program program:
                    return EvalProgram(program, env, context);
                
                case ExpressionStatement exprStmt:
                    return Eval(exprStmt.Expression, env, context);
                
                case IntegerLiteral intLit:
                    return FenValue.FromNumber(intLit.Value);
                
                case BooleanLiteral boolLit:
                    return FenValue.FromBoolean(boolLit.Value);
                
                case StringLiteral strLit:
                    return FenValue.FromString(strLit.Value);

                case NullLiteral _:
                    return FenValue.Null;

                case UndefinedLiteral _:
                    return FenValue.Undefined;

                case PrefixExpression prefixExpr:
                    var right = Eval(prefixExpr.Right, env, context);
                    if (IsError(right)) return right;
                    return EvalPrefixExpression(prefixExpr.Operator, right);

                case InfixExpression infixExpr:
                    var left = Eval(infixExpr.Left, env, context);
                    if (IsError(left)) return left;
                    var rightInfix = Eval(infixExpr.Right, env, context);
                    if (IsError(rightInfix)) return rightInfix;
                    return EvalInfixExpression(infixExpr.Operator, left, rightInfix);

                case BlockStatement blockStmt:
                    return EvalBlockStatement(blockStmt, env, context);

                case IfExpression ifExpr:
                    return EvalIfExpression(ifExpr, env, context);

                case ReturnStatement returnStmt:
                    var val = Eval(returnStmt.ReturnValue, env, context);
                    if (IsError(val)) return val;
                    return new ReturnValue(val);

                case LetStatement letStmt:
                    var valLet = Eval(letStmt.Value, env, context);
                    if (IsError(valLet)) return valLet;

                    if (letStmt.DestructuringPattern != null)
                    {
                        return EvalDestructuringAssignment(letStmt.DestructuringPattern, valLet, env, context);
                    }

                    env.Set(letStmt.Name.Value, valLet);
                    return valLet;

                case Identifier ident:
                    return EvalIdentifier(ident, env);

                case FunctionLiteral funcLit:
                    return FenValue.FromFunction(new FenFunction(funcLit.Parameters, funcLit.Body, env));

                case MemberExpression memberExpr:
                    return EvalMemberExpression(memberExpr, env, context);

                case IndexExpression indexExpr:
                    return EvalIndexExpression(indexExpr, env, context);

                case NewExpression newExpr:
                    return EvalNewExpression(newExpr, env, context);

                case AssignmentExpression assignExpr:
                    // Evaluate the right side first
                    var value = Eval(assignExpr.Right, env, context);
                    if (IsError(value)) return value;
                    
                    // Handle destructuring assignment
                    if (assignExpr.Left is ArrayLiteral || assignExpr.Left is ObjectLiteral)
                    {
                        return EvalDestructuringAssignment(assignExpr.Left, value, env, context);
                    }
                    
                    // Handle assignment to identifier (var x = ...)
                    if (assignExpr.Left is Identifier leftIdent)
                    {
                        env.Update(leftIdent.Value, value);
                        return value;
                    }
                    
                    // Handle assignment to member expression (obj.prop = ...)
                    if (assignExpr.Left is MemberExpression leftMember)
                    {
                        var targetObj = Eval(leftMember.Object, env, context);
                        if (IsError(targetObj)) return targetObj;
                        
                        try { System.IO.File.AppendAllText("debug_log.txt", $"[Interpreter] Assigning to member: {leftMember.Property}. Object type: {targetObj.Type}\r\n"); } catch { }

                        if (targetObj.IsObject)
                        {
                            var targetObjVal = targetObj.AsObject();
                            if (targetObjVal != null)
                            {
                                targetObjVal.Set(leftMember.Property, value);
                                return value;
                            }
                        }
                        else
                        {
                            try { System.IO.File.AppendAllText("debug_log.txt", $"[Interpreter] Cannot assign to non-object: {targetObj.Type}\r\n"); } catch { }
                        }
                    }
                    
                    return FenValue.Undefined;

                case CallExpression callExpr:
                    IValue function = null;
                    IValue thisContext = null;

                    if (callExpr.Function is MemberExpression me)
                    {
                        var obj = Eval(me.Object, env, context);
                        if (IsError(obj)) return obj;
                        
                        if (obj.IsObject)
                        {
                            var targetObj = obj.AsObject();
                            if (targetObj != null)
                            {
                                function = targetObj.Get(me.Property);
                                thisContext = obj; // Bind 'this' to the object
                            }
                        }
                        
                        if (function == null) function = FenValue.Undefined;
                    }
                    else
                    {
                        function = Eval(callExpr.Function, env, context);
                    }

                    if (IsError(function)) return function;
                    
                    var args = EvalExpressionsWithSpread(callExpr.Arguments, env, context);
                    if (args.Count == 1 && IsError(args[0])) return args[0];

                    return ApplyFunction(function, args, context, thisContext);

                case TryStatement tryStmt:
                    return EvalTryStatement(tryStmt, env, context);

                case ThrowStatement throwStmt:
                    return EvalThrowStatement(throwStmt, env, context);

                case WhileStatement whileStmt:
                    return EvalWhileStatement(whileStmt, env, context);

                case ForStatement forStmt:
                    return EvalForStatement(forStmt, env, context);

                case ArrayLiteral arrayLit:
                    return EvalArrayLiteral(arrayLit, env, context);

                case ObjectLiteral objectLit:
                    return EvalObjectLiteral(objectLit, env, context);

                // Ternary conditional: condition ? consequent : alternate
                case ConditionalExpression condExpr:
                    return EvalConditionalExpression(condExpr, env, context);

                case ClassStatement classStmt:
                    return EvalClassStatement(classStmt, env, context);

                case ImportDeclaration importDecl:
                    return EvalImportDeclaration(importDecl, env, context);

                case ExportDeclaration exportDecl:
                    return EvalExportDeclaration(exportDecl, env, context);

                // Arrow function: (params) => body
                case ArrowFunctionExpression arrowExpr:
                    return EvalArrowFunctionExpression(arrowExpr, env);

                case AsyncFunctionExpression asyncExpr:
                    return EvalAsyncFunctionExpression(asyncExpr, env);

                case AwaitExpression awaitExpr:
                    return EvalAwaitExpression(awaitExpr, env, context);

                case RegexLiteral regexLit:
                    return EvalRegexLiteral(regexLit, env);

                // for-in loop: for (x in obj) { ... }
                case ForInStatement forInStmt:
                    return EvalForInStatement(forInStmt, env, context);

                // for-of loop: for (x of iterable) { ... }
                case ForOfStatement forOfStmt:
                    return EvalForOfStatement(forOfStmt, env, context);



                // Empty expression (for recovery)
                case EmptyExpression:
                    return FenValue.Undefined;

                // Switch statement
                case SwitchStatement switchStmt:
                    return EvalSwitchStatement(switchStmt, env, context);

                // Break statement
                case BreakStatement breakStmt:
                    return new BreakValue();

                // Continue statement
                case ContinueStatement continueStmt:
                    return new ContinueValue();

                // Do-while loop
                case DoWhileStatement doWhileStmt:
                    return EvalDoWhileStatement(doWhileStmt, env, context);

            }

            return FenValue.Null;
        }

        private IValue EvalArrayLiteral(ArrayLiteral node, FenEnvironment env, IExecutionContext context)
        {
            var elements = EvalExpressionsWithSpread(node.Elements, env, context);
            if (elements.Count == 1 && IsError(elements[0])) return elements[0];
            
            // For now, represent array as a FenObject with numeric keys and length
            var arrayObj = new FenObject();
            for (int i = 0; i < elements.Count; i++)
            {
                arrayObj.Set(i.ToString(), elements[i]);
            }
            arrayObj.Set("length", FenValue.FromNumber(elements.Count));
            
            return FenValue.FromObject(arrayObj);
        }

        private IValue EvalObjectLiteral(ObjectLiteral node, FenEnvironment env, IExecutionContext context)
        {
            var obj = new FenObject();
            foreach (var pair in node.Pairs)
            {
                var val = Eval(pair.Value, env, context);
                if (IsError(val)) return val;
                obj.Set(pair.Key, val);
            }
            return FenValue.FromObject(obj);
        }

        private IValue EvalProgram(Program program, FenEnvironment env, IExecutionContext context)
        {
            IValue result = FenValue.Null;

            foreach (var stmt in program.Statements)
            {
                result = Eval(stmt, env, context);

                if (result is ReturnValue returnValue)
                {
                    return returnValue.Value;
                }
                
                if (result is ErrorValue)
                {
                    return result;
                }
            }

            return result;
        }

        private IValue EvalBlockStatement(BlockStatement block, FenEnvironment env, IExecutionContext context)
        {
            IValue result = FenValue.Null;

            foreach (var stmt in block.Statements)
            {
                result = Eval(stmt, env, context);

                if (result != null && (
                    result.Type == JsValueType.ReturnValue || 
                    result.Type == JsValueType.Error ||
                    result is BreakValue ||
                    result is ContinueValue))
                {
                    return result;
                }
            }

            return result;
        }

        private IValue EvalPrefixExpression(string op, IValue right)
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
                    // Prefix increment - return incremented value
                    if (right.Type == JsValueType.Number)
                        return FenValue.FromNumber(right.ToNumber() + 1);
                    return FenValue.FromNumber(double.NaN);
                case "--":
                    // Prefix decrement - return decremented value
                    if (right.Type == JsValueType.Number)
                        return FenValue.FromNumber(right.ToNumber() - 1);
                    return FenValue.FromNumber(double.NaN);
                default:
                    return new ErrorValue($"unknown operator: {op}{right.Type}");
            }
        }

        private IValue EvalTypeofExpression(IValue val)
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

        private IValue EvalBangOperatorExpression(IValue right)
        {
            return FenValue.FromBoolean(!right.ToBoolean());
        }

        private IValue EvalMinusPrefixOperatorExpression(IValue right)
        {
            if (right.Type != JsValueType.Number)
            {
                return new ErrorValue($"unknown operator: -{right.Type}");
            }
            return FenValue.FromNumber(-right.ToNumber());
        }

        private IValue EvalInfixExpression(string op, IValue left, IValue right)
        {
            // Handle postfix ++/-- (right is null)
            if (op == "++" && right == null)
            {
                // Postfix increment - return original value
                return left;
            }
            if (op == "--" && right == null)
            {
                // Postfix decrement - return original value
                return left;
            }

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

            // instanceof - simplified check
            if (op == "instanceof")
            {
                return FenValue.FromBoolean(false);  // Simplified
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
                return EvalStringInfixExpression(op, left, right);
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
                return new ErrorValue($"type mismatch: {left.Type} {op} {right.Type}");
            }
            return new ErrorValue($"unknown operator: {left.Type} {op} {right.Type}");
        }

        private IValue EvalIntegerInfixExpression(string op, IValue left, IValue right)
        {
            var leftVal = left.ToNumber();
            var rightVal = right.ToNumber();

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
                    return new ErrorValue($"unknown operator: {left.Type} {op} {right.Type}");
            }
        }

        private IValue EvalStringInfixExpression(string op, IValue left, IValue right)
        {
            if (op != "+")
            {
                return new ErrorValue($"unknown operator: {left.Type} {op} {right.Type}");
            }
            return FenValue.FromString(left.ToString() + right.ToString());
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
            if (val == null)
            {
                return new ErrorValue($"identifier not found: {node.Value}");
            }
            return val;
        }

        private List<IValue> EvalExpressions(List<Expression> exps, FenEnvironment env, IExecutionContext context)
        {
            var result = new List<IValue>();

            foreach (var e in exps)
            {
                var evaluated = Eval(e, env, context);
                if (IsError(evaluated))
                {
                    return new List<IValue> { evaluated };
                }
                result.Add(evaluated);
            }

            return result;
        }

        private IValue ApplyFunction(IValue fn, List<IValue> args, IExecutionContext context, IValue thisContext = null)
        {
            var function = fn.AsFunction();
            if (function != null)
            {
                // Handle native functions
                if (function.IsNative)
                {
                    // Native functions might need 'this' too, but let's skip for now or pass it if signature allows
                    return function.Invoke(args.ToArray(), context);
                }
                
                // Handle user-defined functions
                context.PushCallFrame(function.Name ?? "anonymous");
                var extendedEnv = ExtendFunctionEnv(function, args, context, thisContext);
                var evaluated = Eval(function.Body, extendedEnv, context);
                context.PopCallFrame();
                return UnwrapReturnValue(evaluated);
            }
            
            return new ErrorValue($"not a function: {fn.Type}");
        }

        private FenEnvironment ExtendFunctionEnv(FenFunction fn, List<IValue> args, IExecutionContext context, IValue thisContext = null)
        {
            var env = new FenEnvironment(fn.Env);
            
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

            for (int i = 0; i < fn.Parameters.Count; i++)
            {
                var param = fn.Parameters[i];
                
                if (param.IsRest)
                {
                    var restArray = new FenObject();
                    int restIndex = 0;
                    for (int j = i; j < args.Count; j++)
                    {
                        restArray.Set(restIndex.ToString(), args[j]);
                        restIndex++;
                    }
                    restArray.Set("length", FenValue.FromNumber(restIndex));
                    env.Set(param.Value, FenValue.FromObject(restArray));
                    break; // Rest parameter must be last
                }

                if (i < args.Count && !args[i].IsUndefined)
                {
                    env.Set(param.Value, args[i]);
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

        private IValue UnwrapReturnValue(IValue obj)
        {
            if (obj is ReturnValue returnValue)
            {
                return returnValue.Value;
            }
            return obj;
        }

        private bool IsTruthy(IValue obj)
        {
            return obj.ToBoolean();
        }

        private bool IsError(IValue obj)
        {
            if (obj != null)
            {
                return obj.Type == JsValueType.Error;
            }
            return false;
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
                var val = proto.Get(me.Property);
                if (val != null) return val;
            }
            else if (left.IsObject)
            {
                var obj = left.AsObject();
                if (obj != null)
                {
                    var val = obj.Get(me.Property);
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
                    var val = obj.Get(index.ToString());
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
            if (fn == null)
            {
                return new ErrorValue($"not a constructor: {function.Type}");
            }

            // Create new instance
            var instance = new FenObject();
            if (fn.Prototype != null)
            {
                instance.SetPrototype(fn.Prototype);
            }
            
            // Create new environment for the constructor call
            var newEnv = new FenEnvironment(fn.Env);
            
            // Bind 'this' to the new instance
            newEnv.Set("this", FenValue.FromObject(instance));

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

            // If constructor returns an object, return it. Otherwise return 'this' (instance)
            var returnValue = UnwrapReturnValue(result);
            if (returnValue.IsObject)
            {
                return returnValue;
            }

            return FenValue.FromObject(instance);
        }

        private IValue EvalThrowStatement(ThrowStatement ts, FenEnvironment env, IExecutionContext context)
        {
            var val = Eval(ts.Value, env, context);
            if (IsError(val)) return val;

            return new ErrorValue(val.ToString());
        }

        private IValue EvalTryStatement(TryStatement ts, FenEnvironment env, IExecutionContext context)
        {
            var result = Eval(ts.Block, env, context);

            if (result is ErrorValue errorVal && ts.CatchBlock != null)
            {
                var catchEnv = new FenEnvironment(env);
                if (ts.CatchParameter != null)
                {
                    catchEnv.Set(ts.CatchParameter.Value, FenValue.FromString(errorVal.Message));
                }
                result = Eval(ts.CatchBlock, catchEnv, context);
            }

            if (ts.FinallyBlock != null)
            {
                Eval(ts.FinallyBlock, env, context);
            }

            return result;
        }

        private IValue EvalWhileStatement(WhileStatement ws, FenEnvironment env, IExecutionContext context)
        {
            IValue result = FenValue.Null;

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
                    if (result is ReturnValue || result is ErrorValue) return result;
                    if (result is BreakValue) return FenValue.Null;
                    if (result is ContinueValue) continue;
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

            IValue result = FenValue.Null;

            while (true)
            {
                if (fs.Condition != null)
                {
                    var condition = Eval(fs.Condition, loopEnv, context);
                    if (IsError(condition)) return condition;
                    if (!IsTruthy(condition)) break;
                }

                result = Eval(fs.Body, loopEnv, context);

                if (result != null)
                {
                    if (result is ReturnValue || result is ErrorValue) return result;
                    if (result is BreakValue) return FenValue.Null;
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

            IValue result = FenValue.Null;

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
                        
                        if (result != null)
                        {
                            if (result is ReturnValue || result is ErrorValue) return result;
                            if (result is BreakValue) return FenValue.Null;
                            if (result is ContinueValue) continue;
                        }
                    }
                }
            }

            return result;
        }

        // Evaluate for-of: for (x of iterable) { ... }
        private IValue EvalForOfStatement(ForOfStatement fs, FenEnvironment env, IExecutionContext context)
        {
            var loopEnv = new FenEnvironment(env);
            var iterableVal = Eval(fs.Iterable, env, context);
            if (IsError(iterableVal)) return iterableVal;

            IValue result = FenValue.Null;

            // Handle arrays
            if (iterableVal.IsObject)
            {
                var obj = iterableVal.AsObject();
                if (obj != null)
                {
                    // Check if it's an array (has length property and numeric keys)
                    // For now, we'll just iterate over numeric keys up to length
                    if (obj.Has("length"))
                    {
                        var lenVal = obj.Get("length");
                        if (lenVal.IsNumber)
                        {
                            int len = (int)lenVal.ToNumber();
                            for (int i = 0; i < len; i++)
                            {
                                var val = obj.Get(i.ToString());
                                loopEnv.Set(fs.Variable.Value, val);

                                result = Eval(fs.Body, loopEnv, context);

                                if (result != null)
                                {
                                    if (result is ReturnValue || result is ErrorValue) return result;
                                    if (result is BreakValue) return FenValue.Null;
                                    if (result is ContinueValue) continue;
                                }
                            }
                            return result;
                        }
                    }
                    
                    // Fallback: iterate over values of keys (like for-in but values)
                    // This is not strictly correct for for-of (which uses iterator protocol),
                    // but it's a reasonable approximation for plain objects if someone tries it.
                    // However, standard JS throws "not iterable" for plain objects.
                    // Let's stick to array-like iteration for now.
                }
            }
            else if (iterableVal.IsString)
            {
                // Iterate over characters
                string str = iterableVal.ToString();
                foreach (char c in str)
                {
                    loopEnv.Set(fs.Variable.Value, FenValue.FromString(c.ToString()));
                    
                    result = Eval(fs.Body, loopEnv, context);
                    
                    if (result != null)
                    {
                        if (result is ReturnValue || result is ErrorValue) return result;
                        if (result is BreakValue) return FenValue.Null;
                        if (result is ContinueValue) continue;
                    }
                }
                return result;
            }

            return new ErrorValue($"{iterableVal} is not iterable");
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
            return FenValue.FromFunction(new FenFunction(ae.Parameters, ae.Body, env));
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
                if (c.Test == null) continue; // Skip default for now

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
                    if (c.Test == null)
                    {
                        match = c;
                        break;
                    }
                }
            }

            // Execute cases
            if (match != null)
            {
                IValue result = FenValue.Null;
                int index = ss.Cases.IndexOf(match);
                
                for (int i = index; i < ss.Cases.Count; i++)
                {
                    var currentCase = ss.Cases[i];
                    foreach (var stmt in currentCase.Consequent)
                    {
                        result = Eval(stmt, env, context);
                        
                        if (result is ReturnValue || result is ErrorValue) return result;
                        if (result is BreakValue) return FenValue.Null; // Break exits switch
                        if (result is ContinueValue) return new ErrorValue("Illegal continue statement: no surrounding loop");
                    }
                }
                return result;
            }

            return FenValue.Null;
        }

        // Evaluate do-while loop
        private IValue EvalDoWhileStatement(DoWhileStatement ds, FenEnvironment env, IExecutionContext context)
        {
            IValue result = FenValue.Null;
            var loopEnv = new FenEnvironment(env);

            do
            {
                result = Eval(ds.Body, loopEnv, context);

                if (result is ReturnValue || result is ErrorValue) return result;
                if (result is BreakValue) return FenValue.Null;
                if (result is ContinueValue) 
                {
                    // Continue goes to condition check
                }

                var condition = Eval(ds.Condition, loopEnv, context);
                if (IsError(condition)) return condition;
                if (!IsTruthy(condition)) break;

            } while (true);

            return result;
        }

        private IValue EvalDestructuringAssignment(Expression pattern, IValue value, FenEnvironment env, IExecutionContext context)
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
                            var val = obj.Get(i.ToString()) ?? FenValue.Undefined;

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
                            var val = obj.Get(key) ?? FenValue.Undefined;

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
                    var methodFunc = new FenFunction(method.Value.Parameters, method.Value.Body, env);
                    prototype.Set(method.Key.Value, FenValue.FromFunction(methodFunc));
                }
            }

            // Link constructor and prototype
            constructor.Prototype = prototype;
            
            // For now, let's bind the class name to the constructor function
            env.Set(stmt.Name.Value, FenValue.FromFunction(constructor));
            
            return FenValue.Undefined;
        }

        private List<IValue> EvalExpressionsWithSpread(List<Expression> exps, FenEnvironment env, IExecutionContext context)
        {
            var result = new List<IValue>();

            foreach (var e in exps)
            {
                if (e is SpreadElement spread)
                {
                    var val = Eval(spread.Argument, env, context);
                    if (IsError(val)) return new List<IValue> { val };

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
                        return new List<IValue> { evaluated };
                    }
                    result.Add(evaluated);
                }
            }

            return result;
        }


        public Dictionary<string, IValue> Exports { get; } = new Dictionary<string, IValue>();

        private IValue EvalImportDeclaration(ImportDeclaration stmt, FenEnvironment env, IExecutionContext context)
        {
            if (context.ModuleLoader == null) return FenValue.Undefined;

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

            return _stringPrototype;
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
                return new ErrorValue($"Invalid regular expression: {ex.Message}");
            }
        }
    }

    // Helper classes for internal interpreter state
    public class ReturnValue : IValue
    {
        public IValue Value { get; }
        public JsValueType Type => JsValueType.ReturnValue;

        public ReturnValue(IValue value)
        {
            Value = value;
        }

        public bool ToBoolean() => Value.ToBoolean();
        public double ToNumber() => Value.ToNumber();
        public override string ToString() => Value.ToString();
        public IObject ToObject() => Value.ToObject();
        public bool StrictEquals(IValue other) => Value.StrictEquals(other);
        public bool LooseEquals(IValue other) => Value.LooseEquals(other);
        public bool IsUndefined => false;
        public bool IsNull => false;
        public bool IsBoolean => false;
        public bool IsNumber => false;
        public bool IsString => false;
        public bool IsObject => false;
        public bool IsFunction => false;
        public FenFunction AsFunction() => null;
        public IObject AsObject() => null;
    }

    public class ErrorValue : IValue
    {
        public string Message { get; }
        public JsValueType Type => JsValueType.Error;

        public ErrorValue(string message)
        {
            Message = message;
        }

        public bool ToBoolean() => false;
        public double ToNumber() => double.NaN;
        public override string ToString() => $"Error: {Message}";
        public IObject ToObject() => null;
        public bool StrictEquals(IValue other) => false;
        public bool LooseEquals(IValue other) => false;
        public bool IsUndefined => false;
        public bool IsNull => false;
        public bool IsBoolean => false;
        public bool IsNumber => false;
        public bool IsString => false;
        public bool IsObject => false;
        public bool IsFunction => false;
        public FenFunction AsFunction() => null;
        public IObject AsObject() => null;

    }

    // Break control flow value
    public class BreakValue : IValue
    {
        public JsValueType Type => JsValueType.Undefined;
        public bool ToBoolean() => false;
        public double ToNumber() => double.NaN;
        public override string ToString() => "break";
        public IObject ToObject() => null;
        public bool StrictEquals(IValue other) => false;
        public bool LooseEquals(IValue other) => false;
        public bool IsUndefined => true;
        public bool IsNull => false;
        public bool IsBoolean => false;
        public bool IsNumber => false;
        public bool IsString => false;
        public bool IsObject => false;
        public bool IsFunction => false;
        public FenFunction AsFunction() => null;
        public IObject AsObject() => null;
    }

    // Continue control flow value
    public class ContinueValue : IValue
    {
        public JsValueType Type => JsValueType.Undefined;
        public bool ToBoolean() => false;
        public double ToNumber() => double.NaN;
        public override string ToString() => "continue";
        public IObject ToObject() => null;
        public bool StrictEquals(IValue other) => false;
        public bool LooseEquals(IValue other) => false;
        public bool IsUndefined => true;
        public bool IsNull => false;
        public bool IsBoolean => false;
        public bool IsNumber => false;
        public bool IsString => false;
        public bool IsObject => false;
        public bool IsFunction => false;
        public FenFunction AsFunction() => null;
        public IObject AsObject() => null;
    }
}

