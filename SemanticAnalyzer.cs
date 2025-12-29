using System;
using System.Collections.Generic;

public class SemanticAnalyzer : AlgolSubsetBaseVisitor<object>
{
    // Hierarchická tabulka symbolù pro podporu vnoøených rozsahù
    private Stack<Dictionary<string, SymbolInfo>> scopes = new Stack<Dictionary<string, SymbolInfo>>();
    private Dictionary<string, SymbolInfo> currentScope => scopes.Peek();

    public class SymbolInfo
    {
        public string Type { get; set; }
        public SymbolKind Kind { get; set; }
        public string ReturnType { get; set; } // Pro funkce
        public bool IsArray { get; set; }
        public bool IsFunctionType { get; set; } // NOVÉ: Je to funkèní typ?
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

        // Registrovat vestavìné symboly (napø. print), aby se nehlásily jako nedeclarované
        // (atributy lze rozšíøit pozdìji, pokud budete kontrolovat signatury)
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
        // Hledá symbol od aktuálního rozsahu smìrem ke globálnímu
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

        // Zpracování parametrù
        if (context.param_list() != null)
        {
            Visit(context.param_list());
        }

        // Zpracování tìla procedury (mùže obsahovat vnoøené deklarace)
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

        // Zpracování parametrù
        if (context.param_list() != null)
        {
            Visit(context.param_list());
        }

        // Zpracování tìla funkce (mùže obsahovat vnoøené deklarace)
        Visit(context.block());

        ExitScope();
        return null;
    }

    public override object VisitParam(AlgolSubsetParser.ParamContext context)
    {
        string name = context.IDENT().GetText();
        string type = context.type().GetText();
        bool isArray = context.type().array_type() != null;
        bool isFunctionType = context.type().function_type() != null; // NOVÉ: Kontrola funkèního typu

        // NOVÉ: Rozlišení funkèního parametru
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
            Console.WriteLine($"Info: Parametr '{name}' je funkèního typu: {type}");
        }

        return null;
    }

    public override object VisitBlock(AlgolSubsetParser.BlockContext context)
    {
        // Zpracování všech deklarací a pøíkazù v bloku
        return VisitChildren(context);
    }

    public override object VisitAssignment(AlgolSubsetParser.AssignmentContext context)
    {
        string name = context.IDENT().GetText();
        var symbol = LookupSymbol(name);
        
        if (symbol == null)
        {
            Console.WriteLine($"Chyba: Promìnná '{name}' není deklarována.");
        }
        else if (symbol.Kind != SymbolKind.Variable)
        {
            Console.WriteLine($"Chyba: '{name}' není promìnná (je to {symbol.Kind}).");
        }

        // Zkontrolovat pravou stranu pøiøazení
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
            Console.WriteLine($"Chyba: '{name}' není procedura ani funkce.");
        }
        else
        {
            int currentScopeLevel = 0;
            foreach (var scope in scopes)
            {
                if (scope.ContainsKey(name))
                {
                    string scopeType = currentScopeLevel == scopes.Count - 1 ? "globální" : "lokální";
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
        // Po zmìnì gramatiky máme samostatné tokeny INT_LITERAL a REAL_LITERAL.
        // Nevyvolávají chybu nedeclarovaného identifikátoru, proto je ignorujeme zde.
        if (context.INT_LITERAL() != null || context.REAL_LITERAL() != null || context.STRING() != null)
        {
            return null; // literály jsou v poøádku
        }

        if (context.IDENT() != null && context.expression().Length == 0 && context.procedure_call() == null)
        {
            // Samotný identifikátor (promìnná nebo pole jako celek)
            string name = context.IDENT().GetText();
            var symbol = LookupSymbol(name);
            
            if (symbol == null)
            {
                Console.WriteLine($"Chyba: Identifikátor '{name}' není deklarován.");
            }
        }
        return VisitChildren(context);
    }
}
