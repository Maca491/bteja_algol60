grammar AlgolSubset;

program : block EOF | (declaration | statement ';')* EOF ;

declaration
    : variable_decl
    | procedure_decl
    | function_decl
    | import_decl
    ;

import_decl
    : 'import' STRING ';'
    ;

variable_decl
    : 'var' ident_list ':' type ';'
    ;

ident_list
    : IDENT (',' IDENT)*
    ;

type
    : simple_type
    | array_type
    ;

simple_type
    : 'int'
    | 'real'
    | 'string'
    ;

array_type
    : 'array' '[' dimensions ']' 'of' type
    ;

dimensions
    : range (',' range)*
    ;

range
    : expression '..' expression
    ;

procedure_decl
    : 'procedure' IDENT '(' param_list? ')' ';' block
    ;

function_decl
    : 'function' IDENT '(' param_list? ')' ':' type ';' block
    ;

param_list
    : param (',' param)*
    ;

param
    : ('value')? IDENT ':' type
    ;

block
    : 'begin' (declaration | statement ';')* 'end'
    ;

statement
    : assignment
    | if_statement
    | for_statement
    | procedure_call
    | return_statement
    ;

assignment
    : (IDENT | IDENT '[' expression (',' expression)* ']') ':=' expression
    ;

if_statement
    : 'if' expression 'then' statement ('else' statement)?
    ;

for_statement
    : 'for' IDENT ':=' expression ('step' expression)? 'until' expression 'do' statement
    ;

return_statement
    : 'return' expression
    ;

procedure_call
    : IDENT '(' (expression (',' expression)*)? ')'
    ;

expression
    : simple_expr (rel_op simple_expr)?
    ;

rel_op
    : '=' | '!=' | '<' | '<=' | '>' | '>='
    ;

simple_expr
    : term (('+' | '-') term)*
    ;

term
    : factor (('*' | '/') factor)*
    ;

factor
    : NUMBER
    | STRING
    | IDENT                                              // OK - promìnná nebo pole jako celek
    | procedure_call
    | IDENT '[' expression (',' expression)* ']' 
    | '(' expression ')'
    ;

IDENT
    : [a-zA-Z_] [a-zA-Z0-9_]*
    ;

NUMBER
    : [0-9]+ ('.' [0-9]+)?
    ;

STRING
    : '"' (~["])* '"'
    ;

WS
    : [ \t\r\n]+ -> skip
    ;

// Jednoøádkové komentáøe
COMMENT
    : '//' ~[\r\n]* -> skip
    ;

// Víceøádkové komentáøe
BLOCK_COMMENT
    : '/*' .*? '*/' -> skip
    ;