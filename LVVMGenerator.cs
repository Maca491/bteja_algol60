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
        // Každý scope udržuje záznamy o nově deklarovaných jménech a (pokud byly předtím přepsány) jejich předchozích hodnotách.
        private readonly Stack<Dictionary<string, ScopeEntry>> _scopeStack = new Stack<Dictionary<string, ScopeEntry>>();

        private LLVMValueRef _printfFunc;
        private LLVMValueRef _currentFunction;
        private LLVMTypeRef _currentReturnType; // Aktuální návratový typ funkce

        // Pomocná třída pro uložení informací o položce scope
        private class ScopeEntry
        {
            public LLVMValueRef Ptr;
            public LLVMTypeRef Type;
            public bool HadPrev;
            public LLVMValueRef PrevPtr;
            public LLVMTypeRef PrevType;

            public ScopeEntry(LLVMValueRef ptr, LLVMTypeRef type, bool hadPrev = false, LLVMValueRef prevPtr = default, LLVMTypeRef prevType = default)
            {
                Ptr = ptr;
                Type = type;
                HadPrev = hadPrev;
                PrevPtr = prevPtr;
                PrevType = prevType;
            }
        }

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
            _scopeStack.Push(new Dictionary<string, ScopeEntry>());
        }

        private void PopScope()
        {
            if (_scopeStack.Count > 0)
            {
                var scope = _scopeStack.Pop();
                // Při odstranění scope obnovíme původní hodnoty, pokud existovaly,
                // jinak položku z úplné tabulky odstraníme.
                foreach (var kv in scope)
                {
                    var key = kv.Key;
                    var entry = kv.Value;
                    if (entry.HadPrev)
                    {
                        _namedValues[key] = (entry.PrevPtr, entry.PrevType);
                    }
                    else
                    {
                        _namedValues.Remove(key);
                    }
                }
            }
        }

        private LLVMTypeRef GetLLVMType(string typeName)
        {
            if (typeName.StartsWith("array"))
            {
                // Parsování: "array[1..3,1..3]ofint"
                var ofIndex = typeName.IndexOf("of");
                var elementTypeName = typeName.Substring(ofIndex + 2).Trim();
                var elementType = GetLLVMType(elementTypeName);

                // Extrakce rozměrů: [1..3, 1..3]
                var dimsStart = typeName.IndexOf('[') + 1;
                var dimsEnd = typeName.IndexOf(']');
                var dimsStr = typeName.Substring(dimsStart, dimsEnd - dimsStart);
                var dimensions = dimsStr.Split(',');

                LLVMTypeRef arrayType = elementType;
                foreach (var dim in dimensions.Reverse())
                {
                    var range = dim.Split(new[] { ".." }, StringSplitOptions.None);
                    int start = int.Parse(range[0].Trim());
                    int end = int.Parse(range[1].Trim());
                    int size = end - start + 1;

                    arrayType = LLVMTypeRef.CreateArray(arrayType, (uint)size);
                }
                return arrayType;
            }

            // Rozpoznání funkčního typu: "function(...):ret"
            if (typeName.TrimStart().StartsWith("function"))
            {
                int pStart = typeName.IndexOf('(');
                int pEnd = typeName.IndexOf(')');
                var paramTypes = new List<LLVMTypeRef>();
                if (pStart >= 0 && pEnd > pStart)
                {
                    var inner = typeName.Substring(pStart + 1, pEnd - pStart - 1).Trim();
                    if (!string.IsNullOrEmpty(inner))
                    {
                        var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts)
                        {
                            paramTypes.Add(GetLLVMType(p.Trim()));
                        }
                    }
                }

                int colon = typeName.IndexOf(':', pEnd >= 0 ? pEnd : 0);
                string retName = colon >= 0 ? typeName.Substring(colon + 1).Trim() : "int";
                var retType = GetLLVMType(retName);

                var funcType = LLVMTypeRef.CreateFunction(retType, paramTypes.ToArray(), false);
                // Zde použijeme statickou metodu CreatePointer
                return LLVMTypeRef.CreatePointer(funcType, 0);
            }

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
            _currentReturnType = _i32Type; // nastavit očekávaný návratový typ pro top-level (main)
            _builder.PositionAtEnd(entryBlock);

            base.VisitProgram(context);

            // Pokud je aktuální blok bez terminátoru, přidáme návrat (kontrolujeme vložený blok, ne jen entry)
            var insertBlock = _builder.InsertBlock;
            if (insertBlock.Handle != IntPtr.Zero && insertBlock.Terminator.Handle == IntPtr.Zero)
            {
                _builder.BuildRet(LLVMValueRef.CreateConstInt(_i32Type, 0, false));
            }

            return mainFunc;
        }

        // Upravit VisitFunction_decl: uložit/obnovit _currentReturnType
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
    var previousReturnType = _currentReturnType;

    _currentFunction = function;
    _currentReturnType = returnType;

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
        
        // Uložíme do hlavní tabulky, ale zachováme případnou předchozí hodnotu v scope záznamu
        bool hadPrev = _namedValues.TryGetValue(paramNames[i], out var prev);
        _namedValues[paramNames[i]] = (alloca, paramTypes[i]);
        if (_scopeStack.Count > 0)
            _scopeStack.Peek()[paramNames[i]] = new ScopeEntry(alloca, paramTypes[i], hadPrev, prev.ptr, prev.type);
    }

    // Zpracování těla funkce
    Visit(context.block());

    // Kontrola terminátoru
    if (entryBlock.Terminator.Handle == IntPtr.Zero)
    {
        if (_currentReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
        {
            _builder.BuildRetVoid();
        }
        else
        {
            _builder.BuildRet(LLVMValueRef.CreateConstInt(_currentReturnType, 0, false));
        }
    }

    // Návrat k předchozímu stavu
    PopScope();
    _currentFunction = previousFunction;
    _currentReturnType = previousReturnType;
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

                // Pokud už existuje proměnná se stejným jménem v okolním scope, uložíme její hodnotu
                bool hadPrev = _namedValues.TryGetValue(name, out var prev);
                _namedValues[name] = (alloca, llvmType);
                
                // Pokud jsme ve funkci, přidáme do current scope záznam s informací o případné předchozí hodnotě
                if (_scopeStack.Count > 0)
                {
                    _scopeStack.Peek()[name] = new ScopeEntry(alloca, llvmType, hadPrev, prev.ptr, prev.type);
                }
            }
            return default;
        }

        // Upravit VisitAssignment - zakázat automatickou konverzi, hlásit chybu při mismatch
public override LLVMValueRef VisitAssignment(AlgolSubsetParser.AssignmentContext context)
{
    string name = context.IDENT().GetText();

    if (!_namedValues.ContainsKey(name))
    {
        Console.Error.WriteLine($"Chyba: Proměnná '{name}' neexistuje.");
        return default;
    }

    var (ptr, targetType) = _namedValues[name];

    // Zpracování indexů pole (mat[i, j])
    if (context.expression().Length > 1)
    {
        var indices = new List<LLVMValueRef> { LLVMValueRef.CreateConstInt(_i32Type, 0, false) };

        for (int i = 0; i < context.expression().Length - 1; i++)
        {
            var index = Visit(context.expression(i));
            if (index.Handle == IntPtr.Zero) { Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit index pole."); return default; }
            var adjustedIndex = _builder.BuildSub(index,
                LLVMValueRef.CreateConstInt(_i32Type, 1, false), "adjustedIdx");
            indices.Add(adjustedIndex);
        }

        var elementPtr = _builder.BuildGEP2(targetType, ptr, indices.ToArray(), "arrayPtr");

        int valueExprIndex = context.expression().Length - 1;
        var assignedValue = Visit(context.expression(valueExprIndex));
        if (assignedValue.Handle == IntPtr.Zero) { Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit výraz pro přiřazení do pole."); return default; }

        // Získání typu prvku pole
        var elementType = GetArrayElementType(targetType);

        // Přísné porovnání typů - žádná automatická konverze
        if (!TypesEqual(assignedValue.TypeOf, elementType))
        {
            Console.Error.WriteLine($"Chyba typů: nelze přiřadit hodnotu typu '{assignedValue.TypeOf.Kind}' do prvku pole typu '{elementType.Kind}'.");
            return default;
        }

        _builder.BuildStore(assignedValue, elementPtr);
        return assignedValue;
    }
    
    // Skalární proměnné
    int exprIndex = context.expression().Length - 1;
            LLVMValueRef value = Visit(context.expression(exprIndex));
            
            if (value.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine($"Chyba: Nepodařilo se vyhodnotit výraz pro proměnnou '{name}'.");
                return default;
            }

            var valueType = value.TypeOf;

            // Přísné porovnání typů - žádná automatická konverze
            if (!TypesEqual(valueType, targetType))
            {
                Console.Error.WriteLine($"Chyba typů: nelze přiřadit hodnotu typu '{valueType.Kind}' do proměnné '{name}' typu '{targetType.Kind}'.");
                return default;
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

        // Upravit VisitReturn_statement - bez automatické konverze, porovnat s _currentReturnType
public override LLVMValueRef VisitReturn_statement(AlgolSubsetParser.Return_statementContext context)
{
    var returnValue = Visit(context.expression());

    if (returnValue.Handle == IntPtr.Zero)
    {
        Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit návratovou hodnotu.");
        return default;
    }

    if (_currentReturnType.Handle == IntPtr.Zero)
    {
        Console.Error.WriteLine("Chyba: Interní: není znám očekávaný návratový typ funkce.");
        return default;
    }

    if (!TypesEqual(returnValue.TypeOf, _currentReturnType))
    {
        Console.Error.WriteLine($"Chyba typů při return: hodnota typu '{returnValue.TypeOf.Kind}' neodpovídá očekávanému '{_currentReturnType.Kind}'.");
        return default;
    }

    _builder.BuildRet(returnValue);
    return returnValue;
}

        // Upravit VisitSimple_expr - zakázat implicitní int->double/vice konverze
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

        // Přísné porovnání typů - bez automatické konverze
        if (!TypesEqual(left.TypeOf, right.TypeOf))
        {
            Console.Error.WriteLine($"Chyba typů v aritmetickém výrazu: levý operand '{left.TypeOf.Kind}', pravý operand '{right.TypeOf.Kind}'.");
            return default;
        }

        var kind = left.TypeOf.Kind;

        if (kind == LLVMTypeKind.LLVMDoubleTypeKind)
        {
            if (op == "+") left = _builder.BuildFAdd(left, right, "addtmp");
            else if (op == "-") left = _builder.BuildFSub(left, right, "subtmp");
            else { Console.Error.WriteLine($"Chyba: Nepodporovaný operátor '{op}' pro typ double."); return default; }
        }
        else if (kind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            if (op == "+") left = _builder.BuildAdd(left, right, "addtmp");
            else if (op == "-") left = _builder.BuildSub(left, right, "subtmp");
            else { Console.Error.WriteLine($"Chyba: Nepodporovaný operátor '{op}' pro typ int."); return default; }
        }
        else
        {
            Console.Error.WriteLine($"Chyba: Aritmetika není podporována pro typ '{kind}'.");
            return default;
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

        // Upravit VisitTerm analogicky k VisitSimple_expr (bez implicitních konverzí)
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

        if (!TypesEqual(left.TypeOf, right.TypeOf))
        {
            Console.Error.WriteLine($"Chyba typů v termu: levý operand '{left.TypeOf.Kind}', pravý operand '{right.TypeOf.Kind}'.");
            return default;
        }

        var kind = left.TypeOf.Kind;

        if (kind == LLVMTypeKind.LLVMDoubleTypeKind)
        {
            if (op == "*") left = _builder.BuildFMul(left, right, "multmp");
            else if (op == "/") left = _builder.BuildFDiv(left, right, "divtmp");
            else { Console.Error.WriteLine($"Chyba: Nepodporovaný operátor '{op}' pro typ double."); return default; }
        }
        else if (kind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            if (op == "*") left = _builder.BuildMul(left, right, "multmp");
            else if (op == "/") left = _builder.BuildSDiv(left, right, "divtmp");
            else { Console.Error.WriteLine($"Chyba: Nepodporovaný operátor '{op}' pro typ int."); return default; }
        }
        else
        {
            Console.Error.WriteLine($"Chyba: Operace v termu není podporována pro typ '{kind}'.");
            return default;
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

                // Pokud jde o jméno deklarované funkce, vraťme její LLVM hodnotu (funkce jako hodnota)
                if (_functions.TryGetValue(name, out var funcInfo))
                {
                    return funcInfo.func;
                }

                if (_namedValues.TryGetValue(name, out var varInfo))
                {
                    var (ptr, type) = varInfo;
                    // (indexování pole - stávající kód)
                    if (context.expression().Length > 0)
                    {
                        var indices = new List<LLVMValueRef> { LLVMValueRef.CreateConstInt(_i32Type, 0, false) };

                        foreach (var expr in context.expression())
                        {
                            var index = Visit(expr);
                            var adjustedIndex = _builder.BuildSub(index,
                                LLVMValueRef.CreateConstInt(_i32Type, 1, false), "adjustedIdx");
                            indices.Add(adjustedIndex);
                        }

                        var elementPtr = _builder.BuildGEP2(type, ptr, indices.ToArray(), "arrayPtr");

                        var elementType = GetArrayElementType(type);

                        return _builder.BuildLoad2(elementType, elementPtr, "arrayElement");
                    }

                    // Skalární proměnná
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
                if (val.Handle == IntPtr.Zero) { Console.Error.WriteLine("Chyba: Nepodařilo se vyhodnotit argument pro print."); return default; }

                var valType = val.TypeOf;
                string format;
                if (valType.Kind == LLVMTypeKind.LLVMDoubleTypeKind) format = "%f\n";
                else if (valType.Kind == LLVMTypeKind.LLVMPointerTypeKind) format = "%s\n";
                else format = "%d\n";

                var formatStr = _builder.BuildGlobalStringPtr(format, "fmt");
                var args = new[] { formatStr, val };

                var printfFuncType = LLVMTypeRef.CreateFunction(_i32Type, new[] { _i8PtrType }, true);
                return _builder.BuildCall2(printfFuncType, _printfFunc, args, "printCall");
            }

            // 1) Pokusíme se najít proměnnou obsahující pointer na funkci (nejprve hlavní tabulka, pak scope stack)
    bool foundVar = _namedValues.TryGetValue(name, out var varInfo);

    if (!foundVar)
    {
        foreach (var scope in _scopeStack)
        {
            if (scope.TryGetValue(name, out var scopedEntry)) // scopedEntry je ScopeEntry
            {
                // Převést ScopeEntry na tuple očekávaný _namedValues a varInfo
                varInfo = (scopedEntry.Ptr, scopedEntry.Type);
                _namedValues[name] = varInfo; // uložíme tuple, ne ScopeEntry
                foundVar = true;
                break;
            }
        }
    }

    if (foundVar)
    {
        var (ptr, varType) = varInfo;

        // Pokud pointer invalidní, považujeme to za chybu
        if (ptr.Handle == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Chyba: Interní: proměnná '{name}' existuje, ale pointer je neplatný.");
            return default;
        }

        // Načteme uloženou hodnotu (should be pointer-to-function nebo funkční hodnota)
        var fnPtrVal = _builder.BuildLoad2(varType, ptr, name + "_val");
        if (fnPtrVal.Handle == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Chyba: Nepodařilo se načíst hodnotu funkční proměnné '{name}'.");
            return default;
        }

        // Sestavíme argumenty (zatím bez bitcastu funkčních argumentů)
        var argsList = new List<LLVMValueRef>();
        foreach (var expr in context.expression())
        {
            var arg = Visit(expr);
            if (arg.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine($"Chyba: Nepodařilo se vyhodnotit argument pro volání '{name}'.");
                return default;
            }
            argsList.Add(arg);
        }

        // Robustní odvození LLVMFunctionType:
        LLVMTypeRef funcType = default;

        // 1) Zkusíme deklarovaný typ proměnné (varType) -- očekáváme pointer-to-function nebo pointer-to-pointer...
        if (varType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            var el = varType.ElementType;
            if (el.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
                funcType = el;
            else if (el.Kind == LLVMTypeKind.LLVMPointerTypeKind && el.ElementType.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
                funcType = el.ElementType;
        }
        else if (varType.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
        {
            funcType = varType;
        }

        // 2) Pokud se nepodařilo z deklarace, zkusíme odvodit z načtené hodnoty (fnPtrVal)
        if (funcType.Handle == IntPtr.Zero)
        {
            if (fnPtrVal.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                var el = fnPtrVal.TypeOf.ElementType;
                if (el.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
                    funcType = el;
                else if (el.Kind == LLVMTypeKind.LLVMPointerTypeKind && el.ElementType.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
                    funcType = el.ElementType;
            }
            else if (fnPtrVal.TypeOf.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
            {
                funcType = fnPtrVal.TypeOf;
            }
        }

        // 3) Poslední náhradní varianta: sestavíme jednoduchý LLVMFunctionType z typů argumentů a použijeme i32 návrat (bezpečnostní fallback)
        if (funcType.Handle == IntPtr.Zero)
        {
            var paramTypes = new LLVMTypeRef[argsList.Count];
            for (int i = 0; i < argsList.Count; i++)
                paramTypes[i] = argsList[i].TypeOf;
            funcType = LLVMTypeRef.CreateFunction(_i32Type, paramTypes, false);
        }

        // Připravíme callee jako pointer-to-function odpovídající funcType (bitcast pokud je třeba)
        var expectedCalleeType = LLVMTypeRef.CreatePointer(funcType, 0);
        LLVMValueRef callee = fnPtrVal;
        if (!(fnPtrVal.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && fnPtrVal.TypeOf.ElementType.Kind == LLVMTypeKind.LLVMFunctionTypeKind
          && fnPtrVal.TypeOf.ElementType.Handle == funcType.Handle))
        {
            callee = _builder.BuildBitCast(fnPtrVal, expectedCalleeType, name + "_callee_cast");
        }

        // Přetypování argumentů, které jsou function-values -> pointer-to-function
        for (int i = 0; i < argsList.Count; i++)
        {
            var a = argsList[i];
            if (a.TypeOf.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
            {
                var p = LLVMTypeRef.CreatePointer(a.TypeOf, 0);
                argsList[i] = _builder.BuildBitCast(a, p, "arg_fn_to_ptr");
            }
        }

        // Volání přes odvozený funkční typ a callee (pointer-to-function)
        return _builder.BuildCall2(funcType, callee, argsList.ToArray(), "calltmp");
    }

    // 2) Volání pojmenované (globální) funkce
    if (_functions.TryGetValue(name, out var funcInfo))
    {
        var (func, returnType) = funcInfo;
        var args = new List<LLVMValueRef>();
        var paramTypes = new List<LLVMTypeRef>();

        foreach (var expr in context.expression())
        {
            var arg = Visit(expr);
            if (arg.Handle == IntPtr.Zero) { Console.Error.WriteLine($"Chyba: Nepodařilo se vyhodnotit argument pro funkci '{name}'."); return default; }

            if (arg.TypeOf.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
            {
                var ptrType = LLVMTypeRef.CreatePointer(arg.TypeOf, 0);
                arg = _builder.BuildBitCast(arg, ptrType, "fn_to_ptr");
            }

            args.Add(arg);
            paramTypes.Add(arg.TypeOf);
        }

        var fType = LLVMTypeRef.CreateFunction(returnType, paramTypes.ToArray(), false);
        return _builder.BuildCall2(fType, func, args.ToArray(), "calltmp");
    }

    Console.Error.WriteLine($"Chyba: Neznámá procedura '{name}'.");
    return default;
}

        // Pomocné metody - vložte do třídy
        private LLVMTypeRef GetArrayElementType(LLVMTypeRef arrayType)
        {
            var current = arrayType;
            // Ošetřit pointer na pole i přímo pole – opravena konstanta a precedence podmínek
            while (current.Kind == LLVMTypeKind.LLVMArrayTypeKind
                   || (current.Kind == LLVMTypeKind.LLVMPointerTypeKind && current.ElementType.Kind == LLVMTypeKind.LLVMArrayTypeKind))
            {
                // pokud je pointer na array (např. global string ptr), vezmeme element
                current = current.ElementType;
            }

            // Pokud je stále pole, vrátíme element type (např. vícerozměrné pole)
            while (current.Kind == LLVMTypeKind.LLVMArrayTypeKind)
            {
                current = current.ElementType;
            }

            return current;
        }

    private bool TypesEqual(LLVMTypeRef a, LLVMTypeRef b)
    {
        if (a.Handle == IntPtr.Zero || b.Handle == IntPtr.Zero) return false;

        // Rychlá kontrola podle Kind
        if (a.Kind != b.Kind) return false;

        // Speciální porovnání pro funkční typy — porovnáme textovou reprezentaci typu
        if (a.Kind == LLVMTypeKind.LLVMFunctionTypeKind)
        {
            // LLVMTypeRef nemá FunctionType vlastnost v této binding verzi,
            // proto porovnáme canonical string reprezentaci typu.
            return a.ToString() == b.ToString();
        }

        if (a.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            // porovnáme element typ pointeru
            return a.ElementType.Kind == b.ElementType.Kind;
        }

        // Pro ostatní typy stačí shoda Kind
        return true;
    }
        }
    }