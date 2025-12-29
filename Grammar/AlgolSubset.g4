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
    | function_type 
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
    : INT_LITERAL
    | REAL_LITERAL
    | STRING
    | IDENT                                              
    | procedure_call
    | IDENT '[' expression (',' expression)* ']' 
    | '(' expression ')'
    ;

IDENT
    : [a-zA-Z_] [a-zA-Z0-9_]*
    ;

INT_LITERAL
    : [0-9]+
    ;

REAL_LITERAL
    : [0-9]+ '.' [0-9]+
    ;

STRING
    : '"' (~["])* '"'
    ;

WS
    : [ \t\r\n]+ -> skip
    ;

function_type
    : 'function' '(' type_list? ')' ':' type
    ;

type_list
    : type (',' type)*
    ;

//Jednořádkové komentáře
COMMENT
    : '//' ~[\r\n]* -> skip
    ;

//Víceřádkové komentáře
BLOCK_COMMENT
    : '/*' .*? '*/' -> skip
    ;