using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.FenEngine.Core
{
    public class Interpreter
    {
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
                    env.Set(letStmt.Name.Value, valLet);
                    return valLet; // Or undefined?

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
                    var function = Eval(callExpr.Function, env, context);
                    if (IsError(function)) return function;
                    
                    var args = EvalExpressions(callExpr.Arguments, env, context);
                    if (args.Count == 1 && IsError(args[0])) return args[0];

                    return ApplyFunction(function, args, context);

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




            }

            return FenValue.Null;
        }

        private IValue EvalArrayLiteral(ArrayLiteral node, FenEnvironment env, IExecutionContext context)
        {
            var elements = EvalExpressions(node.Elements, env, context);
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

                if (result != null && (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error))
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
                default:
                    return new ErrorValue($"unknown operator: {op}{right.Type}");
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
                case "<":
                    return FenValue.FromBoolean(leftVal < rightVal);
                case ">":
                    return FenValue.FromBoolean(leftVal > rightVal);
                case "==":
                    return FenValue.FromBoolean(leftVal == rightVal);
                case "!=":
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

        private IValue ApplyFunction(IValue fn, List<IValue> args, IExecutionContext context)
        {
            var function = fn.AsFunction();
            if (function != null)
            {
                // Handle native functions
                if (function.IsNative)
                {
                    return function.Invoke(args.ToArray(), context);
                }
                
                // Handle user-defined functions
                context.PushCallFrame(function.Name ?? "anonymous");
                var extendedEnv = ExtendFunctionEnv(function, args);
                var evaluated = Eval(function.Body, extendedEnv, context);
                context.PopCallFrame();
                return UnwrapReturnValue(evaluated);
            }
            
            return new ErrorValue($"not a function: {fn.Type}");
        }

        private FenEnvironment ExtendFunctionEnv(FenFunction fn, List<IValue> args)
        {
            var env = new FenEnvironment(fn.Env);

            for (int i = 0; i < fn.Parameters.Count; i++)
            {
                if (i < args.Count)
                {
                    env.Set(fn.Parameters[i].Value, args[i]);
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

            if (left.IsObject)
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

            var args = EvalExpressions(node.Arguments, env, context);
            if (args.Count == 1 && IsError(args[0])) return args[0];

            var fn = function.AsFunction();
            if (fn == null)
            {
                return new ErrorValue($"not a constructor: {function.Type}");
            }

            // Create new instance
            var instance = new FenObject();
            
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

                if (result != null && (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error))
                {
                    return result;
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

                if (result != null && (result.Type == JsValueType.ReturnValue || result.Type == JsValueType.Error))
                {
                    return result;
                }

                if (fs.Update != null)
                {
                    var update = Eval(fs.Update, loopEnv, context);
                    if (IsError(update)) return update;
                }
            }

            return result;
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
}
