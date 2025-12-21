using System;
using System.Collections.Generic;
using Antlr4.Runtime.Tree;
using LLVMSharp.Interop;

namespace Compiler
{
    public unsafe class LLVMGenerator : AlgolSubsetBaseVisitor<LLVMValueRef>
    {
        private readonly LLVMContextRef _context;
        private readonly LLVMModuleRef _module;
        private readonly LLVMBuilderRef _builder;

        private readonly LLVMTypeRef _i32Type;
        private readonly LLVMTypeRef _doubleType;
        private readonly LLVMTypeRef _i8PtrType;

        // Tabulka proměnných: název -> (pointer, typ)
        private readonly Dictionary<string, (LLVMValueRef ptr, LLVMTypeRef type)> _namedValues = new Dictionary<string, (LLVMValueRef, LLVMTypeRef)>();
        
        // Tabulka funkcí: název -> (funkce, návratový typ)
        private readonly Dictionary<string, (LLVMValueRef func, LLVMTypeRef returnType)> _functions = new Dictionary<string, (LLVMValueRef, LLVMTypeRef)>();

        // Stack pro vnořené scope (pro lokální proměnné ve funkcích)
        private readonly Stack<Dictionary<string, (LLVMValueRef ptr, LLVMTypeRef type)>> _scopeStack = new Stack<Dictionary<string, (LLVMValueRef, LLVMTypeRef)>>();

        private LLVMValueRef _printfFunc;
        private LLVMValueRef _currentFunction;

        public LLVMGenerator()
        {
            _context = LLVMContextRef.Global;
            _module = _context.CreateModuleWithName("AlgolProgram");
            _builder = _context.CreateBuilder();

            _i32Type = LLVMTypeRef.Int32;
            _doubleType = LLVMTypeRef.Double;
            _i8PtrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

            InitializeExternalFunctions();
        }

        private void InitializeExternalFunctions()
        {
            var paramTypes = new[] { _i8PtrType };
            var printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, paramTypes, true);
            _printfFunc = _module.AddFunction("printf", printfType);
        }

        public LLVMModuleRef GetModule() => _module;

        private void PushScope()
        {
            _scopeStack.Push(new Dictionary<string, (LLVMValueRef, LLVMTypeRef)>());
        }

        private void PopScope()
        {
            if (_scopeStack.Count > 0)
            {
                var scope = _scopeStack.Pop();
                // Odstranění lokálních proměnných z hlavní tabulky
                foreach (var key in scope.Keys)
                {
                    _namedValues.Remove(key);
                }
            }
        }

        private LLVMTypeRef GetLLVMType(string typeName)
        {
            if (typeName.Contains("real"))
                return _doubleType;
            else if (typeName.Contains("string"))
                return _i8PtrType;
            else
                return _i32Type;
        }

        public override LLVMValueRef VisitProgram(AlgolSubsetParser.ProgramContext context)
        {
            var funcType = LLVMTypeRef.CreateFunction(_i32Type, Array.Empty<LLVMTypeRef>(), false);
            var mainFunc = _module.AddFunction("main", funcType);
            var entryBlock = _context.AppendBasicBlock(mainFunc, "entry");

            _currentFunction = mainFunc;
            _builder.PositionAtEnd(entryBlock);

            base.VisitProgram(context);

            if (entryBlock.Terminator.Handle == IntPtr.Zero)
            {
                _builder.BuildRet(LLVMValueRef.CreateConstInt(_i32Type, 0, false));
            }

            return mainFunc;
        }

        public override LLVMValueRef VisitFunction_decl(AlgolSubsetParser.Function_declContext context)
        {
            string funcName = context.IDENT().GetText();
            string returnTypeName = context.type().GetText();
            LLVMTypeRef returnType = GetLLVMType(returnTypeName);

            // Zpracování parametrů
            var paramTypes = new List<LLVMTypeRef>();
            var paramNames = new List<string>();

            if (context.param_list() != null)
            {
                foreach (var param in context.param_list().param())
                {
                    string paramName = param.IDENT().GetText();
                    string paramTypeName = param.type().GetText();
                    LLVMTypeRef paramType = GetLLVMType(paramTypeName);
                    
                    paramTypes.Add(paramType);
                    paramNames.Add(paramName);
                }
            }

            // Vytvoření funkce
            var funcType = LLVMTypeRef.CreateFunction(returnType, paramTypes.ToArray(), false);
            var function = _module.AddFunction(funcName, funcType);

            // Uložení do tabulky funkcí
            _functions[funcName] = (function, returnType);

            // Uložení současného stavu
            var previousFunction = _currentFunction;
            var previousBlock = _builder.InsertBlock;

            _currentFunction = function;

            // Vytvoření entry bloku
            var entryBlock = _context.AppendBasicBlock(function, "entry");
            _builder.PositionAtEnd(entryBlock);

            // Nový scope pro parametry a lokální proměnné
            PushScope();

            // Alokace a uložení parametrů
            for (int i = 0; i < paramNames.Count; i++)
            {
                var param = function.GetParam((uint)i);
                param.Name = paramNames[i];
                
                var alloca = _builder.BuildAlloca(paramTypes[i], paramNames[i]);
                _builder.BuildStore(param, alloca);
                
                _namedValues[paramNames[i]] = (alloca, paramTypes[i]);
                if (_scopeStack.Count > 0)
                    _scopeStack.Peek()[paramNames[i]] = (alloca, paramTypes[i]);
            }

            // Zpracování těla funkce
            Visit(context.block());

            // Kontrola terminátoru
            if (entryBlock.Terminator.Handle == IntPtr.Zero)
            {
                if (returnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
                {
                    _builder.BuildRetVoid();
                }
                else
                {
                    _builder.BuildRet(LLVMValueRef.CreateConstInt(returnType, 0, false));
                }
            }

            // Návrat k předchozímu stavu
            PopScope();
            _currentFunction = previousFunction;
            if (previousBlock.Handle != IntPtr.Zero)
            {
                _builder.PositionAtEnd(previousBlock);
            }

            return function;
        }

        public override LLVMValueRef VisitVariable_decl(AlgolSubsetParser.Variable_declContext context)
        {
            string typeName = context.type().GetText();
            LLVMTypeRef llvmType = GetLLVMType(typeName);

            foreach (var ident in context.ident_list().IDENT())
            {
                string name = ident.GetText();
                var alloca = _builder.BuildAlloca(llvmType, name);
                _namedValues[name] = (alloca, llvmType);
                
                // Pokud jsme ve funkci, přidáme do current scope
                if (_scopeStack.Count > 0)
                {
                    _scopeStack.Peek()[name] = (alloca, llvmType);
                }
            }
            return default;
        }

        public override LLVMValueRef VisitAssignment(AlgolSubsetParser.AssignmentContext context)
        {
            string name = context.IDENT().GetText();

            if (!_namedValues.ContainsKey(name))
            {
                Console.Error.WriteLine($"Chyba: Proměnná '{name}' neexistuje.");
                return default;
            }

            int exprIndex = context.expression().Length - 1;
            LLVMValueRef value = Visit(context.expression(exprIndex));
            
            if (value.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine($"Chyba: Nepodařilo se vyhodnotit výraz pro proměnnou '{name}'.");
                return default;
            }

            var (ptr, targetType) = _namedValues[name];
            
            if (ptr.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine($"Chyba: Neplatný pointer pro proměnnou '{name}'.");
                return default;
            }

            var valueType = value.TypeOf;
            
            if (valueType.Kind != targetType.Kind)
            {
                if (targetType.Kind == LLVMTypeKind.LLVMDoubleTypeKind && valueType.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
                {
                    value = _builder.BuildSIToFP(value, _doubleType, "intToDouble");
                }
                else if (targetType.Kind == LLVMTypeKind.LLVMIntegerTypeKind && valueType.Kind == LLVMTypeKind.LLVMDoubleTypeKind)
                {
                    value = _builder.BuildFPToSI(value, _i32Type, "doubleToInt");
                }
            }

            _builder.BuildStore(value, ptr);

            return value;
        }

        public override LLVMValueRef VisitIf_statement(AlgolSubsetParser.If_statementContext context)
        {
            var condition = Visit(context.expression());
            
            if (condition.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit podmínku if.");
                return default;
            }

            // Vytvoření bloků
            var thenBlock = _context.AppendBasicBlock(_currentFunction, "then");
            var elseBlock = context.statement().Length > 1 ? _context.AppendBasicBlock(_currentFunction, "else") : default;
            var mergeBlock = _context.AppendBasicBlock(_currentFunction, "ifcont");

            // Podmíněný skok
            var condType = condition.TypeOf;
            if (condType.Kind != LLVMTypeKind.LLVMIntegerTypeKind || condType.IntWidth != 1)
            {
                // Konverze na bool (i1)
                condition = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, condition, 
                    LLVMValueRef.CreateConstInt(condition.TypeOf, 0, false), "ifcond");
            }

            if (elseBlock.Handle != IntPtr.Zero)
            {
                _builder.BuildCondBr(condition, thenBlock, elseBlock);
            }
            else
            {
                _builder.BuildCondBr(condition, thenBlock, mergeBlock);
            }

            // Then blok
            _builder.PositionAtEnd(thenBlock);
            Visit(context.statement(0));
            if (thenBlock.Terminator.Handle == IntPtr.Zero)
            {
                _builder.BuildBr(mergeBlock);
            }

            // Else blok (pokud existuje)
            if (elseBlock.Handle != IntPtr.Zero)
            {
                _builder.PositionAtEnd(elseBlock);
                Visit(context.statement(1));
                if (elseBlock.Terminator.Handle == IntPtr.Zero)
                {
                    _builder.BuildBr(mergeBlock);
                }
            }

            // Merge blok
            _builder.PositionAtEnd(mergeBlock);

            return default;
        }

        public override LLVMValueRef VisitFor_statement(AlgolSubsetParser.For_statementContext context)
        {
            string loopVar = context.IDENT().GetText();
            
            if (!_namedValues.ContainsKey(loopVar))
            {
                Console.Error.WriteLine($"Chyba: Proměnná smyčky '{loopVar}' neexistuje.");
                return default;
            }

            var (varPtr, varType) = _namedValues[loopVar];

            // Inicializace
            var startVal = Visit(context.expression(0));
            if (startVal.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit počáteční hodnotu.");
                return default;
            }
            _builder.BuildStore(startVal, varPtr);

            // Krok (pokud existuje)
            var stepVal = context.expression().Length > 1 && context.GetChild(5).GetText() == "step"
                ? Visit(context.expression(1))
                : LLVMValueRef.CreateConstInt(_i32Type, 1, false);

            // Konečná hodnota
            int endExprIndex = context.expression().Length - 1;
            var endVal = Visit(context.expression(endExprIndex));

            // Vytvoření bloků
            var loopBlock = _context.AppendBasicBlock(_currentFunction, "loop");
            var afterBlock = _context.AppendBasicBlock(_currentFunction, "afterloop");

            _builder.BuildBr(loopBlock);
            _builder.PositionAtEnd(loopBlock);

            // Tělo smyčky
            Visit(context.statement());

            // Inkrement
            var currentVal = _builder.BuildLoad2(varType, varPtr, loopVar);
            var nextVal = _builder.BuildAdd(currentVal, stepVal, "nextval");
            _builder.BuildStore(nextVal, varPtr);

            // Podmínka pokračování
            var loopCond = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, nextVal, endVal, "loopcond");
            _builder.BuildCondBr(loopCond, loopBlock, afterBlock);

            _builder.PositionAtEnd(afterBlock);

            return default;
        }

        public override LLVMValueRef VisitReturn_statement(AlgolSubsetParser.Return_statementContext context)
        {
            var returnValue = Visit(context.expression());
            
            if (returnValue.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit návratovou hodnotu.");
                return default;
            }

            var returnType = returnValue.TypeOf;
            
            if (returnType.Kind == LLVMTypeKind.LLVMDoubleTypeKind)
            {
                returnValue = _builder.BuildFPToSI(returnValue, _i32Type, "retCast");
            }
            else if (returnType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                returnValue = LLVMValueRef.CreateConstInt(_i32Type, 0, false);
            }

            _builder.BuildRet(returnValue);
            return returnValue;
        }

        public override LLVMValueRef VisitSimple_expr(AlgolSubsetParser.Simple_exprContext context)
        {
            LLVMValueRef left = Visit(context.term(0));
            
            if (left.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit levý operand výrazu.");
                return default;
            }

            for (int i = 1; i < context.term().Length; i++)
            {
                LLVMValueRef right = Visit(context.term(i));
                
                if (right.Handle == IntPtr.Zero)
                {
                    Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit pravý operand výrazu.");
                    return default;
                }
                
                string op = context.GetChild(2 * i - 1).GetText();

                var leftType = left.TypeOf;
                var rightType = right.TypeOf;
                
                bool isDouble = leftType.Kind == LLVMTypeKind.LLVMDoubleTypeKind || 
                                rightType.Kind == LLVMTypeKind.LLVMDoubleTypeKind;

                if (isDouble)
                {
                    if (leftType.Kind != LLVMTypeKind.LLVMDoubleTypeKind)
                        left = _builder.BuildSIToFP(left, _doubleType, "toDouble");
                    if (rightType.Kind != LLVMTypeKind.LLVMDoubleTypeKind)
                        right = _builder.BuildSIToFP(right, _doubleType, "toDouble");

                    if (op == "+") left = _builder.BuildFAdd(left, right, "addtmp");
                    else if (op == "-") left = _builder.BuildFSub(left, right, "subtmp");
                }
                else
                {
                    if (op == "+") left = _builder.BuildAdd(left, right, "addtmp");
                    else if (op == "-") left = _builder.BuildSub(left, right, "subtmp");
                }
            }
            return left;
        }

        public override LLVMValueRef VisitExpression(AlgolSubsetParser.ExpressionContext context)
        {
            var left = Visit(context.simple_expr(0));
            
            if (context.rel_op() != null && context.simple_expr().Length > 1)
            {
                var right = Visit(context.simple_expr(1));
                string op = context.rel_op().GetText();

                LLVMIntPredicate predicate = op switch
                {
                    "=" => LLVMIntPredicate.LLVMIntEQ,
                    "!=" => LLVMIntPredicate.LLVMIntNE,
                    "<" => LLVMIntPredicate.LLVMIntSLT,
                    "<=" => LLVMIntPredicate.LLVMIntSLE,
                    ">" => LLVMIntPredicate.LLVMIntSGT,
                    ">=" => LLVMIntPredicate.LLVMIntSGE,
                    _ => LLVMIntPredicate.LLVMIntEQ
                };

                var cmp = _builder.BuildICmp(predicate, left, right, "cmptmp");
                // Rozšíření i1 na i32
                return _builder.BuildZExt(cmp, _i32Type, "booltmp");
            }

            return left;
        }

        public override LLVMValueRef VisitTerm(AlgolSubsetParser.TermContext context)
        {
            LLVMValueRef left = Visit(context.factor(0));
            
            if (left.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit levý operand termu.");
                return default;
            }

            for (int i = 1; i < context.factor().Length; i++)
            {
                LLVMValueRef right = Visit(context.factor(i));
                
                if (right.Handle == IntPtr.Zero)
                {
                    Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit pravý operand termu.");
                    return default;
                }
                
                string op = context.GetChild(2 * i - 1).GetText();

                var leftType = left.TypeOf;
                var rightType = right.TypeOf;
                
                bool isDouble = leftType.Kind == LLVMTypeKind.LLVMDoubleTypeKind || 
                                rightType.Kind == LLVMTypeKind.LLVMDoubleTypeKind;

                if (isDouble)
                {
                    if (leftType.Kind != LLVMTypeKind.LLVMDoubleTypeKind)
                        left = _builder.BuildSIToFP(left, _doubleType, "toDouble");
                    if (rightType.Kind != LLVMTypeKind.LLVMDoubleTypeKind)
                        right = _builder.BuildSIToFP(right, _doubleType, "toDouble");

                    if (op == "*") left = _builder.BuildFMul(left, right, "multmp");
                    else if (op == "/") left = _builder.BuildFDiv(left, right, "divtmp");
                }
                else
                {
                    if (op == "*") left = _builder.BuildMul(left, right, "multmp");
                    else if (op == "/") left = _builder.BuildSDiv(left, right, "divtmp");
                }
            }
            return left;
        }

        public override LLVMValueRef VisitFactor(AlgolSubsetParser.FactorContext context)
        {
            if (context.NUMBER() != null)
            {
                string text = context.NUMBER().GetText();
                
                if (text.Contains("."))
                {
                    if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
                    {
                        return LLVMValueRef.CreateConstReal(_doubleType, val);
                    }
                }
                else
                {
                    if (long.TryParse(text, out long val))
                    {
                        return LLVMValueRef.CreateConstInt(_i32Type, (ulong)val, false);
                    }
                }
                Console.Error.WriteLine($"Chyba: Nelze parsovat číslo '{text}'.");
                return default;
            }
            else if (context.STRING() != null)
            {
                string text = context.STRING().GetText();
                text = text.Trim('"');
                
                var strValue = _builder.BuildGlobalStringPtr(text, "str");
                return strValue;
            }
            else if (context.IDENT() != null && context.procedure_call() == null)
            {
                string name = context.IDENT().GetText();
                if (_namedValues.TryGetValue(name, out var varInfo))
                {
                    var (ptr, type) = varInfo;
                    if (ptr.Handle == IntPtr.Zero)
                    {
                        Console.Error.WriteLine($"Chyba: Neplatný pointer pro proměnnou '{name}'.");
                        return default;
                    }
                    return _builder.BuildLoad2(type, ptr, name);
                }
                else
                {
                    Console.Error.WriteLine($"Chyba: Proměnná '{name}' neexistuje.");
                    return default;
                }
            }
            else if (context.expression().Length > 0 && context.IDENT() == null)
            {
                return Visit(context.expression(0));
            }
            else if (context.procedure_call() != null)
            {
                return Visit(context.procedure_call());
            }

            Console.Error.WriteLine("Chyba: Neznámý typ faktoru.");
            return default;
        }

        public override LLVMValueRef VisitProcedure_call(AlgolSubsetParser.Procedure_callContext context)
        {
            string name = context.IDENT().GetText();

            if (name == "print" && context.expression().Length > 0)
            {
                LLVMValueRef val = Visit(context.expression(0));
                
                if (val.Handle == IntPtr.Zero)
                {
                    Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit argument pro print.");
                    return default;
                }

                var valType = val.TypeOf;
                
                string format;
                if (valType.Kind == LLVMTypeKind.LLVMDoubleTypeKind)
                {
                    format = "%f\n";
                }
                else if (valType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
                {
                    format = "%s\n";
                }
                else
                {
                    format = "%d\n";
                }

                var formatStr = _builder.BuildGlobalStringPtr(format, "fmt");
                var args = new[] { formatStr, val };
                
                var printfFuncType = LLVMTypeRef.CreateFunction(_i32Type, 
                    new[] { _i8PtrType }, 
                    true);
                    
                return _builder.BuildCall2(printfFuncType, _printfFunc, args, "printCall");
            }
            
            // Volání uživatelské funkce
            if (_functions.TryGetValue(name, out var funcInfo))
            {
                var (func, returnType) = funcInfo;
                
                // Zpracování argumentů
                var args = new List<LLVMValueRef>();
                foreach (var expr in context.expression())
                {
                    var arg = Visit(expr);
                    if (arg.Handle == IntPtr.Zero)
                    {
                        Console.Error.WriteLine($"Chyba: Nepodařilo se vyhodnotit argument pro funkci '{name}'.");
                        return default;
                    }
                    args.Add(arg);
                }

                var funcType = func.TypeOf.ElementType;
                return _builder.BuildCall2(funcType, func, args.ToArray(), "calltmp");
            }

            Console.Error.WriteLine($"Chyba: Neznámá procedura '{name}'.");
            return default;
        }
    }
}