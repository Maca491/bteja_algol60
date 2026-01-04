using System;
using System.Collections.Generic;

public class SemanticAnalyzer : AlgolSubsetBaseVisitor<object>
{
    // Hierarchická tabulka symbolů pro podporu vnořených rozsahů
    private Stack<Dictionary<string, SymbolInfo>> scopes = new Stack<Dictionary<string, SymbolInfo>>();
    private Dictionary<string, SymbolInfo> currentScope => scopes.Peek();

    public class SymbolInfo
    {
        public string Type { get; set; }
        public SymbolKind Kind { get; set; }
        public string ReturnType { get; set; } // Pro funkce
        public bool IsArray { get; set; }
        public bool IsFunctionType { get; set; } 
    }

    public enum SymbolKind
    {
        Variable,
        Procedure,
        Function
    }

    public SemanticAnalyzer()
    {
        // Globální rozsah
        scopes.Push(new Dictionary<string, SymbolInfo>());

        // Registrovat vestavěné symboly (např. print), aby se nehlásily jako nedeclarované
        DeclareSymbol("print", new SymbolInfo
        {
            Type = "void",
            Kind = SymbolKind.Procedure
        });
    }

    private void EnterScope()
    {
        scopes.Push(new Dictionary<string, SymbolInfo>());
    }

    private void ExitScope()
    {
        scopes.Pop();
    }

    private SymbolInfo LookupSymbol(string name)
    {
        // Hledá symbol od aktuálního rozsahu směrem ke globálnímu
        foreach (var scope in scopes)
        {
            if (scope.ContainsKey(name))
            {
                return scope[name];
            }
        }
        return null;
    }

    private bool DeclareSymbol(string name, SymbolInfo info)
    {
        if (currentScope.ContainsKey(name))
        {
            Console.WriteLine($"Chyba: Symbol '{name}' je již deklarován v aktuálním rozsahu.");
            return false;
        }
        currentScope[name] = info;
        return true;
    }

    private bool IsNumericType(string typeName)
    {
        // Normalizace typu (odstranění mezery a array specifikace)
        var normalizedType = typeName.Trim().ToLower();
        
        if (normalizedType.StartsWith("array"))
        {
            // Pro pole získáme element typ
            var ofIndex = normalizedType.IndexOf("of");
            if (ofIndex >= 0)
            {
                normalizedType = normalizedType.Substring(ofIndex + 2).Trim();
            }
        }
        
        return normalizedType == "int" || normalizedType == "real";
    }

    private string GetExpressionType(AlgolSubsetParser.ExpressionContext context)
    {
        // Zjednodušená inference typu výrazu
        // V reálném analyzátoru by byla komplexnější
        
        if (context.simple_expr().Length == 1)
        {
            return GetSimpleExprType(context.simple_expr(0));
        }
        
        // Relační operace vracejí boolean (reprezentovaný jako int)
        return "int";
    }

    private string GetSimpleExprType(AlgolSubsetParser.Simple_exprContext context)
    {
        // Získat typ prvního termu
        return GetTermType(context.term(0));
    }

    private string GetTermType(AlgolSubsetParser.TermContext context)
    {
        return GetFactorType(context.factor(0));
    }

    private string GetFactorType(AlgolSubsetParser.FactorContext context)
    {
        if (context.INT_LITERAL() != null)
            return "int";
        
        if (context.REAL_LITERAL() != null)
            return "real";
        
        if (context.STRING() != null)
            return "string";
        
        if (context.IDENT() != null && context.procedure_call() == null)
        {
            string name = context.IDENT().GetText();
            var symbol = LookupSymbol(name);
            return symbol?.Type ?? "unknown";
        }
        
        if (context.procedure_call() != null)
        {
            string name = context.procedure_call().IDENT().GetText();
            var symbol = LookupSymbol(name);
            return symbol?.ReturnType ?? "unknown";
        }
        
        if (context.expression().Length > 0 && context.IDENT() == null)
        {
            // Výraz v závorkách
            return GetExpressionType(context.expression(0));
        }
        
        return "unknown";
    }

    public override object VisitVariable_decl(AlgolSubsetParser.Variable_declContext context)
    {
        var varNames = context.ident_list().IDENT();
        var type = context.type().GetText();
        bool isArray = context.type().array_type() != null;

        foreach (var varName in varNames)
        {
            string name = varName.GetText();
            DeclareSymbol(name, new SymbolInfo
            {
                Type = type,
                Kind = SymbolKind.Variable,
                IsArray = isArray
            });
        }
        return null;
    }

    public override object VisitProcedure_decl(AlgolSubsetParser.Procedure_declContext context)
    {
        string name = context.IDENT().GetText();
        
        DeclareSymbol(name, new SymbolInfo
        {
            Type = "void",
            Kind = SymbolKind.Procedure
        });

        // Vstup do nového rozsahu pro proceduru
        EnterScope();

        // Zpracování parametrů
        if (context.param_list() != null)
        {
            Visit(context.param_list());
        }

        // Zpracování těla procedury (může obsahovat vnořené deklarace)
        Visit(context.block());

        ExitScope();
        return null;
    }

    public override object VisitFunction_decl(AlgolSubsetParser.Function_declContext context)
    {
        string name = context.IDENT().GetText();
        string returnType = context.type().GetText();
        
        DeclareSymbol(name, new SymbolInfo
        {
            Type = returnType,
            Kind = SymbolKind.Function,
            ReturnType = returnType
        });

        Console.WriteLine($"Info: Deklarována funkce '{name}' s návratovým typem '{returnType}'");

        // Vstup do nového rozsahu pro funkci
        EnterScope();

        // Zpracování parametrů 
        if (context.param_list() != null)
        {
            Visit(context.param_list());
        }

        // Zpracování těla funkce (může obsahovat vnořené deklarace)
        Visit(context.block());

        ExitScope();
        return null;
    }

    public override object VisitParam(AlgolSubsetParser.ParamContext context)
    {
        string name = context.IDENT().GetText();
        string type = context.type().GetText();
        bool isArray = context.type().array_type() != null;
        bool isFunctionType = context.type().function_type() != null; // NOVĚ: Kontrola funkčního typu

        // Rozlišení funkčního parametru
        var kind = isFunctionType ? SymbolKind.Function : SymbolKind.Variable;

        DeclareSymbol(name, new SymbolInfo
        {
            Type = type,
            Kind = kind,
            IsArray = isArray,
            IsFunctionType = isFunctionType
        });

        if (isFunctionType)
        {
            Console.WriteLine($"Info: Parametr '{name}' je funk�n�ho typu: {type}");
        }

        return null;
    }

    public override object VisitBlock(AlgolSubsetParser.BlockContext context)
    {
        // Zpracování všech deklarací a příkazů v bloku
        return VisitChildren(context);
    }

    public override object VisitAssignment(AlgolSubsetParser.AssignmentContext context)
    {
        string name = context.IDENT().GetText();
        var symbol = LookupSymbol(name);
        
        if (symbol == null)
        {
            Console.WriteLine($"Chyba: Proměnná '{name}' není deklarována.");
        }
        else if (symbol.Kind != SymbolKind.Variable)
        {
            Console.WriteLine($"Chyba: '{name}' není proměnná (je to {symbol.Kind}).");
        }

        // Zkontrolovat pravou stranu přiřazení
        if (context.expression() != null && context.expression().Length > 0)
        {
            Visit(context.expression(0));
        }
        
        return null;
    }

    public override object VisitProcedure_call(AlgolSubsetParser.Procedure_callContext context)
    {
        string name = context.IDENT().GetText();
        var symbol = LookupSymbol(name);
        
        if (symbol == null)
        {
            Console.WriteLine($"Chyba: Procedura/funkce '{name}' není deklarována.");
        }
        else if (symbol.Kind != SymbolKind.Procedure && symbol.Kind != SymbolKind.Function)
        {
            Console.WriteLine($"Chyba: '{name}' není     procedura ani funkce.");
        }
        else
        {
            int currentScopeLevel = 0;
            foreach (var scope in scopes)
            {
                if (scope.ContainsKey(name))
                {
                    string scopeType = currentScopeLevel == scopes.Count - 1 ? "globálně" : "lokálně";
                    Console.WriteLine($"Info: Volána {scopeType} {symbol.Kind.ToString().ToLower()} '{name}'");
                    break;
                }
                currentScopeLevel++;
            }
        }

        // Zkontrolovat argumenty
        if (context.expression() != null)
        {
            foreach (var expr in context.expression())
            {
                Visit(expr);
            }
        }
        return null;
    }

    public override object VisitFactor(AlgolSubsetParser.FactorContext context)
    {
        // Po změně gramatiky máme samostatné tokeny INT_LITERAL a REAL_LITERAL.
        if (context.INT_LITERAL() != null || context.REAL_LITERAL() != null || context.STRING() != null)
        {
            return null; // literály jsou v pořádku
        }

        if (context.IDENT() != null && context.expression().Length == 0 && context.procedure_call() == null)
        {
            // Samotný identifikátor (proměnná nebo pole jako celek)
            string name = context.IDENT().GetText();
            var symbol = LookupSymbol(name);
            
            if (symbol == null)
            {
                Console.WriteLine($"Chyba: Identifikátor '{name}' není deklarován.");
            }
        }
        return VisitChildren(context);
    }

    public override object VisitSimple_expr(AlgolSubsetParser.Simple_exprContext context)
    {
        // Navštívit všechny termy
        foreach (var term in context.term())
        {
            Visit(term);
        }
        
        // Kontrola typů pro aritmetické operace (+, -)
        if (context.term().Length > 1)
        {
            for (int i = 0; i < context.term().Length; i++)
            {
                string termType = GetTermType(context.term(i));
                
                if (!IsNumericType(termType))
                {
                    Console.WriteLine($"Chyba: Aritmetické operace (+/-) není povolena pro typ '{termType}'. Povoleny jsou pouze typy 'int' a 'real'.");
                }
                
                // Kontrola kompatibility typů mezi operandy
                if (i > 0)
                {
                    string prevTermType = GetTermType(context.term(i-1));
                    if (termType != prevTermType && IsNumericType(termType) && IsNumericType(prevTermType))
                    {
                        Console.WriteLine($"Chyba: Nesmíchejte typy v aritmetickém výrazu: '{prevTermType}' a '{termType}'.");
                    }
                }
            }
        }
        
        return null;
    }

    public override object VisitTerm(AlgolSubsetParser.TermContext context)
    {
        // Navštívit všechny faktory
        foreach (var factor in context.factor())
        {
            Visit(factor);
        }
        
        // Kontrola typů pro násobení/dělení (*, /)
        if (context.factor().Length > 1)
        {
            for (int i = 0; i < context.factor().Length; i++)
            {
                string factorType = GetFactorType(context.factor(i));
                
                if (!IsNumericType(factorType))
                {
                    Console.WriteLine($"Chyba: Aritmetické operace (*,/) není povolena pro typ '{factorType}'. Povoleny jsou pouze typy 'int' a 'real'.");
                }
                
                // Kontrola kompatibility typů mezi operandy
                if (i > 0)
                {
                    string prevFactorType = GetFactorType(context.factor(i-1));
                    if (factorType != prevFactorType && IsNumericType(factorType) && IsNumericType(prevFactorType))
                    {
                        Console.WriteLine($"Chyba: Nesmíchejte typy v aritmetickém výrazu: '{prevFactorType}' a '{factorType}'.");
                    }
                }
            }
        }
        
        return null;
    }

    public override object VisitExpression(AlgolSubsetParser.ExpressionContext context)
    {
        // Navštívit všechny simple_expr
        foreach (var simpleExpr in context.simple_expr())
        {
            Visit(simpleExpr);
        }
        
        // Kontrola typů pro relační operace (=, !=, <, <=, >, >=)
        if (context.simple_expr().Length > 1)
        {
            string leftType = GetSimpleExprType(context.simple_expr(0));
            string rightType = GetSimpleExprType(context.simple_expr(1));
            
            if (leftType != rightType)
            {
                Console.WriteLine($"Chyba: Nelze porovnávat různé typy: '{leftType}' a '{rightType}'.");
            }
        }
        
        return null;
    }
}
