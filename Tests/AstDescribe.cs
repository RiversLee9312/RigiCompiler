using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 统一 AST 描述器（M31）：全部测试套件共用一份，替代原先 13+ 份分叉的
    /// Describe*/Format* 私有副本。目标是信息无损的紧凑结构串，用于精确比对：
    /// 同一 AST 树在任何套件里的描述文本一致。
    ///
    /// 格式约定（表达式）：
    ///   Int(42,I32[,hex])  Float(3.14[f])  Str("..."[,interp])  Bool(True)  Null
    ///   StrInterp("lit", expr, ...)（S7f 插值拆分后的段序列，保源码顺序）
    ///   Unary(- x)  Binary(l + r)  CompoundAssign(t op= v)  Group(x)
    ///   Path(head, [.seg, ?.seg, :seg])（M42 统一路径：head 为符号 a / a&lt;T&gt; /
    ///   a(args) / a[0] 或 (expr底座)；段带 &lt;T&gt; 与 (args)/[args] 后缀）
    ///   New(T, [args])  Cast(x as[?] T)  Check(x is T | is .Case)  EnumCase(.N)
    ///   If(c, [then块], [else块])  Switch(s, [p -> b块], default -> d块)（均可带 named 标签）
    ///   SwitchStmt(s, [p -> b块], default -> d块)（语句形态）  TypeOf(x)
    ///   Lambda[ async]([ps])[\&lt;gs&gt;]: R ->[ named L] body（单表达式或 [块]）  Seq(...)
    /// 格式约定（语句/声明）见各方法注释。
    /// </summary>
    public static class AstDescribe
    {
        // ===== 表达式 =====

        public static string Expr(ASTNode? node)
        {
            return node switch
            {
                null => "<null>",
                LiteralExpressionASTNode lit => Expr(lit.Literal),
                IntLiteralASTNode i => $"Int({i.Value},{i.IntType}{IntBase(i)})",
                FloatLiteralASTNode f => $"Float({f.Value}{(f.IsFloat ? "f" : "")})",
                StringLiteralASTNode s => s.InterpolationParts != null
                    ? $"StrInterp({string.Join(", ", s.InterpolationParts.Select(InterpPart))})"
                    : $"Str(\"{s.Value}\"{(s.HasInterpolation ? ",interp" : "")})",
                CharLiteralASTNode c => $"Char('{c.Value}')",
                BoolLiteralASTNode b => $"Bool({b.Value})",
                NullLiteralASTNode => "Null",
                UnaryExpressionASTNode u => $"Unary({u.Operator} {Expr(u.Operand.Expression)})",
                BinaryExpressionASTNode b =>
                    $"Binary({Expr(b.Left.Expression)} {b.Operator} {Expr(b.Right.Expression)})",
                CompoundAssignmentExpressionASTNode ca =>
                    $"CompoundAssign({Expr(ca.Target.Expression)} {ca.Operator}= {Expr(ca.Value.Expression)})",
                GroupExpressionASTNode g => $"Group({Expr(g.InnerExpression.Expression)})",
                PathExpressionASTNode p => Path(p),
                NewExpressionASTNode n =>
                    $"New({Type(n.Type)}, [{string.Join(", ", n.Arguments.Select(Arg))}])",
                CastExpressionASTNode c =>
                    $"Cast({Expr(c.Object.Expression)} as{(c.IsSafe ? "?" : "")} {Type(c.TargetType)})",
                TypeCheckExpressionASTNode t => t.TargetCase != null
                    ? $"Check({Expr(t.Object.Expression)} {t.Operator} {Expr(t.TargetCase)})"
                    : $"Check({Expr(t.Object.Expression)} {t.Operator} {Type(t.TargetType!)})",
                EnumCaseExpressionASTNode ec => $"EnumCase(.{ec.CaseName})",
                IfExpressionASTNode e =>
                    $"If({Expr(e.Condition.Expression)}{Named(e.Label)}, {Block(e.ThenBody)}, {Block(e.ElseBody)})",
                SwitchExpressionASTNode s => Switch(s),
                LambdaExpressionASTNode l => Lambda(l),
                TypeOfExpressionASTNode t => $"TypeOf({Expr(t.Operand.Expression)})",
                SeqBlockExpressionASTNode seq => Seq(seq),
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 字符串插值段（S7f）：字面量段为段级字面量结构（Str 描述），表达式段递归描述
        private static string InterpPart(StringInterpolationPart part)
        {
            return part.Text != null ? Expr(part.Text) : Expr(part.Expression!.Expression);
        }

        // 整数进制后缀：,hex / ,bin / ,oct（十进制无后缀）
        private static string IntBase(IntLiteralASTNode i)
        {
            return i.Base switch
            {                LiteralIntBase.Hex => ",hex",
                LiteralIntBase.Binary => ",bin",
                LiteralIntBase.Octal => ",oct",
                _ => ""
            };
        }

        // Switch(sel[, named L], [p -> [块], ...], default -> [块])（分支体统一为代码块，§7.2）
        private static string Switch(SwitchExpressionASTNode s)
        {
            string cases = string.Join(", ", s.Cases.Select(
                c => $"{Expr(c.Pattern.Expression)} -> {Block(c.Body)}"));
            string def = s.DefaultBody != null ? Block(s.DefaultBody) : "<none>";
            return $"Switch({Expr(s.Selector.Expression)}{Named(s.Label)}, [{cases}], default -> {def})";
        }

        // Lambda[ async]([params])[\<generics>]: Ret ->[ named L] body
        // （body 为单表达式或多语句 [块]，两形态互斥，§5.1）
        private static string Lambda(LambdaExpressionASTNode l)
        {
            string desc = "Lambda";
            if (l.IsAsync) desc += " async";
            desc += $"({Params(l.Parameters)})";
            if (l.GenericParameters != null) desc += Generics(l.GenericParameters);
            desc += $": {Type(l.ReturnType)} ->";
            if (l.Label != null) desc += $" named {l.Label}";
            desc += l.BlockBody != null ? $" {Block(l.BlockBody)}" : $" {Expr(l.Body!.Expression)}";
            return desc;
        }

        // named 标签片段：, named L（无标签为空串）
        private static string Named(string? label) => label != null ? $", named {label}" : "";

        // Seq([volatile, ][using(const f: T = init), ][named L, ][body])
        private static string Seq(SeqBlockExpressionASTNode seq)
        {
            var parts = new List<string>();
            if (seq.IsVolatile) parts.Add("volatile");
            foreach (var binding in seq.UsingBindings)
            {
                var constVar = binding.IsConst ? "const" : "var";
                var type = binding.Type != null ? $": {Type(binding.Type)}" : "";
                parts.Add($"using({constVar} {binding.VariableName}{type} = {Expr(binding.Initializer.Expression)})");
            }
            if (seq.Label != null) parts.Add($"named {seq.Label}");
            parts.Add(Block(seq.Body));
            return $"Seq({string.Join(", ", parts)})";
        }

        // ===== 语句 =====

        public static string Stmt(ASTNode node)
        {
            return node switch
            {
                VariableDeclarationASTNode v => VarDecl(v),
                ExpressionStatementASTNode s => s.AssignValue != null
                    ? $"Assign({Expr(s.Expression.Expression)} = {Expr(s.AssignValue.Expression)})"
                    : Expr(s.Expression.Expression),
                ReturnStatementASTNode r =>
                    $"Return{(r.Label != null ? "@" + r.Label : "")}" +
                    $"{(r.Value != null ? $"({Expr(r.Value!.Expression)})" : "")}",
                LoopControlStatementASTNode l =>
                    $"{(l.IsBreak ? "Break" : "Continue")}{(l.Label != null ? "@" + l.Label : "")}",
                IfStatementASTNode i => IfStmt(i),
                SwitchStatementASTNode s => SwitchStmt(s),
                LoopStatementASTNode l => Loop(l),
                ThrowStatementASTNode t => $"Throw({Expr(t.Exception.Expression)})",
                YieldStatementASTNode y => y.Alarm != null ? $"Yield({Expr(y.Alarm.Expression)})" : "Yield",
                TryCatchFinallyStatementASTNode t => TryCatch(t),
                CodeBlockASTNode b => Block(b),
                ExpressionASTNode e => Expr(e),
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 代码块：[stmt, stmt, ...]
        public static string Block(CodeBlockASTNode block)
        {
            return "[" + string.Join(", ", block.Statements.Select(Stmt)) + "]";
        }

        // IfStmt(cond, [then], [else] / IfStmt(else if) / <none>)
        private static string IfStmt(IfStatementASTNode i)
        {
            string elsePart = i.ElseBranch switch
            {
                null => "<none>",
                CodeBlockASTNode b => Block(b),
                IfStatementASTNode nested => IfStmt(nested),
                _ => $"<{i.ElseBranch.GetType().Name}>"
            };
            return $"IfStmt({Expr(i.Condition.Expression)}, {Block(i.ThenBlock)}, {elsePart})";
        }

        // SwitchStmt(sel, [p -> [块], ...], default -> [块])（语句形态，§7.2）
        private static string SwitchStmt(SwitchStatementASTNode s)
        {
            string cases = string.Join(", ", s.Cases.Select(
                c => $"{Expr(c.Pattern.Expression)} -> {Block(c.Body)}"));
            string def = s.DefaultBody != null ? Block(s.DefaultBody) : "<none>";
            return $"SwitchStmt({Expr(s.Selector.Expression)}, [{cases}], default -> {def})";
        }

        // For(v, [Range(a to b) | iterable][, named L], [body]) / While / DoWhile
        private static string Loop(LoopStatementASTNode l)
        {
            string label = l.Label != null ? $", named {l.Label}" : "";
            return l.Kind switch
            {
                LoopKind.For => l.RangeTo != null
                    ? $"For({l.VariableName}, Range({Expr(l.Iterable!.Expression)} to {Expr(l.RangeTo.Expression)}){label}, {Block(l.Body)})"
                    : $"For({l.VariableName}, {Expr(l.Iterable!.Expression)}{label}, {Block(l.Body)})",
                LoopKind.While =>
                    $"While({Expr(l.Condition!.Expression)}{label}, {Block(l.Body)})",
                _ =>
                    $"DoWhile({Expr(l.Condition!.Expression)}{label}, {Block(l.Body)})"
            };
        }

        // Try([try], [Catch(e: T, [body]), ...][, Finally(e, [body])])
        private static string TryCatch(TryCatchFinallyStatementASTNode t)
        {
            var desc = $"Try({Block(t.TryBlock)}, [{string.Join(", ", t.CatchClauses.Select(Catch))}]";
            if (t.FinallyBlock != null)
            {
                var param = t.FinallyParameter != null ? t.FinallyParameter : "_";
                desc += $", Finally({param}, {Block(t.FinallyBlock)})";
            }
            return desc + ")";
        }

        private static string Catch(CatchClauseASTNode c)
        {
            var varName = c.VariableName ?? "_";
            return $"Catch({varName}: {Type(c.ExceptionType)}, {Block(c.Body)})";
        }

        // ===== 声明 =====

        // 顶层：decl; decl; ...（空为 <empty>）
        public static string Root(RootASTNode root)
        {
            if (root.Declarations.Count == 0) return "<empty>";
            return string.Join("; ", root.Declarations.Select(Decl));
        }

        // 声明（全局/成员/嵌套同一路径）：
        //   [@A @B ]mods class Name[\<gen>][ : Base][ implements I1,I2][ like f][ {members}]
        //   [@A ]mods func Name[\<gen>](params)[: Ret][ {}]   var 见 VarDecl
        //   import a.b.C[.*]   namespace a.b.c
        public static string Decl(ASTNode node)
        {
            var head = node switch
            {
                ClassDeclarationASTNode c => Mods(c.Modifiers) + "class " + c.ClassName
                    + Generics(c.GenericParameters) + Bases(c.BaseClass, c.Interfaces)
                    + (c.LikeTarget != null ? " like " + c.LikeTarget : ""),
                InterfaceDeclarationASTNode i => Mods(i.Modifiers) + "interface " + i.InterfaceName
                    + Generics(i.GenericParameters)
                    + (i.BaseInterfaces.Count > 0
                        ? " : " + string.Join(",", i.BaseInterfaces.Select(Type))
                        : ""),
                StructDeclarationASTNode s => Mods(s.Modifiers) + "struct " + s.StructName
                    + Generics(s.GenericParameters) + Bases(s.BaseStruct, s.Interfaces),
                EnumStructDeclarationASTNode e => Mods(e.Modifiers) + "enum struct " + e.EnumName
                    + Generics(e.GenericParameters),
                WrapperDeclarationASTNode w => Mods(w.Modifiers) + "wrapper " + w.WrapperName
                    + Generics(w.GenericParameters),
                VariableDeclarationASTNode v => VarDecl(v),
                CallableDeclarationASTNode f => Callable(f),
                NamespaceDeclarationASTNode n => "namespace " + Symbol(n.Name.symbol),
                ImportASTNode im => string.Join("; ", im.importedSymbols.Select(ImportItem)),
                _ => $"<{node.GetType().Name}>"
            };

            if (node is not IWrapperAttachable) return head;

            // 变量声明的注解前缀已由 VarDecl 自带（且无成员容器），直接返回
            if (node is VariableDeclarationASTNode) return head;

            // @ 注解渲染在声明头之前（与源码书写位置一致）
            head = Annotations((IWrapperAttachable)node) + head;

            // 类型节点的成员容器（var/func/import 等无成员列表，视为空）
            var members = node switch
            {
                ClassDeclarationASTNode c => c.Members,
                InterfaceDeclarationASTNode i => i.Members,
                StructDeclarationASTNode s => s.Members,
                EnumStructDeclarationASTNode e => e.Members,
                WrapperDeclarationASTNode w => w.Members,
                _ => new List<ASTNode>()
            };

            if (members.Count == 0 && node is not EnumStructDeclarationASTNode) return head;

            var body = members.Count == 0
                ? ""
                : " {" + string.Join(", ", members.Select(Decl)) + "}";
            // enum struct 的 [case 列表] 位于类型体 } 之后（SYNTAX §12）
            var cases = node is EnumStructDeclarationASTNode es ? EnumCases(es) : "";
            return head + body + cases;
        }

        // [@A ] [mods] var|const name[: Type][ {get..., set...}] = init（自带注解前缀，
        // Stmt 与 Decl 两条路径都能渲染注解）
        public static string VarDecl(VariableDeclarationASTNode v)
        {
            // 解构形态（S7f，SYNTAX §18）：var (a, b) = pair——与单名互斥
            var name = v.DestructureNames != null
                ? "(" + string.Join(", ", v.DestructureNames) + ")"
                : v.Name;
            var desc = Annotations(v) + Mods(v.Modifiers) + (v.IsConst ? "const " : "var ") + name;
            if (v.TypeAnnotation != null) desc += $": {Type(v.TypeAnnotation)}";

            var accessors = new List<string>();
            if (v.Getter != null) accessors.Add(Accessor(v.Getter));
            if (v.Setter != null) accessors.Add(Accessor(v.Setter));
            if (accessors.Count > 0) desc += " {" + string.Join(", ", accessors) + "}";

            if (v.Initializer != null) desc += $" = {Expr(v.Initializer.Expression)}";
            return desc;
        }

        // [mods ]get | [mods ]get(value){} | [mods ]set(_){}（无体 = 编译器生成实现）
        public static string Accessor(PropertyAccessorASTNode a)
        {
            var s = a.Modifiers.Count > 0 ? string.Join(" ", a.Modifiers) + " " : "";
            s += a.Kind == AccessorKind.Get ? "get" : "set";
            if (a.Body != null)
                s += a.HasBackingField ? "(value){}" : "(_){}" ;
            return s;
        }

        private static string Callable(CallableDeclarationASTNode f)
        {
            var kind = f.Kind switch
            {
                CallableKind.Operator => "operator ",
                CallableKind.Init => "",
                _ => "func "
            };
            var ret = f.ReturnType != null ? ": " + Type(f.ReturnType) : "";
            var body = f.Body != null ? " {}" : "";
            return Mods(f.Modifiers) + kind + f.Name + Generics(f.GenericParameters)
                + "(" + string.Join(",", f.Parameters.Parameters.Select(Param)) + ")" + ret + body;
        }

        // name[: [named ]Type][ -> field][...][ = default]
        public static string Param(ParameterASTNode p)
        {
            var s = p.Name;
            // init 映射参数可省略类型（沿用字段类型）；普通参数总是带类型
            bool hasType = p.Type.TypeSymbol.symbol.elements.Count > 0;
            if (hasType)
            {
                s += ": " + (p.IsNamedVariadic ? "named " : "") + Type(p.Type);
            }
            if (p.MappedFieldName != null) s += " -> " + p.MappedFieldName;
            if (p.IsVariadic || p.IsNamedVariadic) s += "...";
            if (p.DefaultValue != null) s += $" = {Expr(p.DefaultValue.Expression)}";
            return s;
        }

        public static string Params(ParameterListASTNode list)
        {
            return "[" + string.Join(", ", list.Parameters.Select(Param)) + "]";
        }

        // 泛型形参列表（声明位置）：\<p1, out p2, named p3..., T extends C, S with W>
        public static string Generics(GenericParameterListASTNode? gp)
        {
            if (gp == null) return "";
            var parts = gp.Parameters.Select(GenericParam).ToList();
            parts.AddRange(gp.Constraints.Select(GenericConstraint));
            return "\\<" + string.Join(", ", parts) + ">";
        }

        private static string GenericParam(GenericParameterASTNode p)
        {
            var prefix = p.Variance switch
            {
                GenericVariance.Out => "out ",
                GenericVariance.In => "in ",
                _ => p.IsNamedVariadic ? "named " : ""
            };
            var suffix = (p.IsVariadic || p.IsNamedVariadic) ? "..." : "";
            return prefix + p.Name + suffix;
        }

        private static string GenericConstraint(GenericConstraintASTNode c)
        {
            var kind = c.Kind switch
            {
                GenericConstraintKind.Extends => "extends",
                GenericConstraintKind.Supers => "supers",
                _ => "with"
            };
            return Type(c.Target) + " " + kind + " " + Type(c.Bound);
        }

        private static string EnumCases(EnumStructDeclarationASTNode e)
        {
            if (e.Cases.Count == 0) return "";
            return "[" + string.Join(", ", e.Cases.Select(EnumCase)) + "]";
        }

        private static string EnumCase(EnumCaseASTNode c)
        {
            var s = c.CaseName;
            if (c.Arguments.Count > 0)
                s += "(" + string.Join(", ", c.Arguments.Select(Arg)) + ")";
            if (c.DiscriminantValue != null)
                s += " -> " + c.DiscriminantValue.Value;
            return s;
        }

        private static string ImportItem(ImportItem item)
        {
            var s = "import " + Symbol(item.symbolNode.symbol);
            return item.importAll ? s + ".*" : s;
        }

        // ===== 零件 =====

        // 实参：name:value（TypeDeclarationTests 旧格式 name = value 已统一为冒号形）
        public static string Arg(ArgumentASTNode arg)
        {
            return arg.Name != null
                ? $"{arg.Name}:{Expr(arg.Value.Expression)}"
                : Expr(arg.Value.Expression);
        }

        public static string Type(TypeReferenceASTNode t)
        {
            string typeName = Symbol(t.TypeSymbol.symbol);
            if (t.IsNullable) typeName += "?";
            return typeName;
        }

        // 符号：a.b.c<T,U>（泛型实参递归）
        public static string Symbol(Symbol symbol)
        {
            var parts = new List<string>();
            foreach (var element in symbol.elements)
            {
                string part = element.name;
                if (element.generics.Count > 0)
                {
                    part += "<" + string.Join(",", element.generics.Select(g => Symbol(g))) + ">";
                }
                parts.Add(part);
            }
            return string.Join(".", parts);
        }

        // 成员/符号头上的泛型实参：<T,...>（无实参为空串）
        private static string GenericArgs(List<TypeReferenceASTNode> args)
        {
            if (args.Count == 0) return "";
            return "<" + string.Join(",", args.Select(Type)) + ">";
        }

        // ===== 路径表达式（M42）=====
        // Path(head, [段, ...])；head：a / a<T> / a(args) / a[0] / (expr底座)；
        // 段：.name / ?.name / :name + <T> + (args)/[args] 后缀
        private static string Path(PathExpressionASTNode path)
        {
            return $"Path({PathHead(path.Head)}, [{string.Join(", ", path.Segments.Select(PathSegment))}])";
        }

        private static string PathHead(PathHeadASTNode head)
        {
            var s = head.Name ?? $"({Expr(head.Expression!.Expression)})";
            s += GenericArgs(head.GenericArguments);
            s += string.Join("", head.Suffixes.Select(PathSuffix));
            return s;
        }

        private static string PathSegment(PathSegmentASTNode segment)
        {
            var connector = segment.Connector switch
            {
                PathConnector.SafeDot => "?.",
                PathConnector.Colon => ":",
                _ => ".",
            };
            return connector + segment.Name + GenericArgs(segment.GenericArguments)
                + string.Join("", segment.Suffixes.Select(PathSuffix));
        }

        private static string PathSuffix(PathSuffixASTNode suffix)
        {
            var open = suffix.Kind == PathSuffixKind.Call ? "(" : "[";
            var close = suffix.Kind == PathSuffixKind.Call ? ")" : "]";
            return open + string.Join(", ", suffix.Arguments.Select(Arg)) + close;
        }

        // @ 注解前缀：@Name[(args)] + 空格；无注解为空串
        public static string Annotations(IWrapperAttachable node)
        {
            if (node.Annotations.Count == 0) return "";
            return string.Join(" ", node.Annotations.Select(Annotation)) + " ";
        }

        private static string Annotation(AnnotationASTNode a)
        {
            var s = "@" + Symbol(a.Name.symbol);
            if (a.HasArguments)
                s += "(" + string.Join(", ", a.Arguments.Select(Arg)) + ")";
            return s;
        }

        private static string Mods(List<string> m) => m.Count == 0 ? "" : string.Join(" ", m) + " ";

        private static string Bases(TypeReferenceASTNode? base_, List<TypeReferenceASTNode> ifaces)
        {
            var s = base_ != null ? " : " + Type(base_) : "";
            if (ifaces.Count > 0)
                s += " implements " + string.Join(",", ifaces.Select(Type));
            return s;
        }
    }
}
