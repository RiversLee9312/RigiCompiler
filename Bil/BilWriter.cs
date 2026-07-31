using System.IO;
using System.Text;

namespace LatteCompiler.Bil
{
    // BIL 文本生成器（BIL_STANDARD §4/§5.6/§19）：模型 → 标准 BIL 文本。
    // 只输出标准 spelling（不输出 legacy）；段物理顺序与 §4 一致且全部
    // 输出（空段也输出，§4）；排版严格对齐 §19 黄金示例（4 空格缩进、
    // 段间空行）；换行统一 \n，不随平台漂移。
    public static class BilWriter
    {
        private const string Indent = "    ";

        public static string Write(BilModule module)
        {
            var sb = new StringBuilder();
            Write(module, sb);
            return sb.ToString();
        }

        public static void Write(BilModule module, TextWriter output)
        {
            var sb = new StringBuilder();
            Write(module, sb);
            output.Write(sb.ToString());
        }

        private static void Write(BilModule module, StringBuilder sb)
        {
            sb.Append($"BIL \"{module.BilVersion}\"\n");

            // §4 段顺序：Metadata → Resources → LocalSymbols → ExternalSymbols → 函数
            WriteMetadata(module, sb);
            WriteResources(module, sb);
            WriteSymbolSection(sb, "LocalSymbols", module.LocalSymbols);
            WriteSymbolSection(sb, "ExternalSymbols", module.ExternalSymbols);
            foreach (var function in module.Functions)
            {
                sb.Append('\n');
                WriteFunction(function, sb);
            }
        }

        // ===== 模块级段 =====

        private static void WriteMetadata(BilModule module, StringBuilder sb)
        {
            sb.Append('\n');
            sb.Append("Metadata {\n");
            foreach (var entry in module.Metadata)
            {
                sb.Append($"{Indent}{entry.Key} = {entry.TypeKeyword} {entry.LiteralText}\n");
            }
            sb.Append("}\n");
        }

        private static void WriteResources(BilModule module, StringBuilder sb)
        {
            sb.Append('\n');
            sb.Append("Resources {\n");
            for (int i = 0; i < module.Resources.Count; i++)
            {
                // 条目后逗号（最后一项除外，§19 示例形态）
                var trailing = i < module.Resources.Count - 1 ? "," : "";
                WriteResource(module.Resources[i], sb, Indent, trailing);
            }
            sb.Append("}\n");
        }

        private static void WriteResource(BilResource resource, StringBuilder sb,
            string indent, string trailing)
        {
            switch (resource)
            {
                case BilScalarResource scalar:
                    sb.Append($"{indent}{scalar.Name} = {scalar.TypeKeyword} {scalar.LiteralText}{trailing}\n");
                    break;
                case BilNullResource nullResource:
                    sb.Append($"{indent}{nullResource.Name} = null type({nullResource.TypeRef}){trailing}\n");
                    break;
                case BilCollectionResource collection:
                    if (!collection.Multiline)
                    {
                        sb.Append($"{indent}{collection.Name} = {collection.Header} {{ {string.Join(", ", collection.Elements)} }}{trailing}\n");
                    }
                    else
                    {
                        // §18.2/§18.5 规范排版：map/catch-table 元素各占一行
                        sb.Append($"{indent}{collection.Name} = {collection.Header} {{\n");
                        for (int i = 0; i < collection.Elements.Count; i++)
                        {
                            var comma = i < collection.Elements.Count - 1 ? "," : "";
                            sb.Append($"{indent}{Indent}{collection.Elements[i]}{comma}\n");
                        }
                        sb.Append($"{indent}}}{trailing}\n");
                    }
                    break;
                default:
                    throw new CompilerInternalException($"未知的资源类型: {resource.GetType().Name}");
            }
        }

        // ===== 符号声明（§8）=====

        private static void WriteSymbolSection(StringBuilder sb, string sectionName,
            System.Collections.Generic.List<BilSymbolSectionEntry> entries)
        {
            sb.Append('\n');
            sb.Append($"{sectionName} {{\n");
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case BilTypeDeclaration type:
                        WriteTypeDeclaration(type, sb);
                        break;
                    // 段内裸成员一律单行形态输出；续行形态只存在于类型体内
                    // （§19 wrapper 隐藏字段示例），生成器不产生该组合
                    case BilSimpleMemberDeclaration { ModifiersOnNextLine: true }:
                        throw new CompilerInternalException("段内裸成员声明不支持修饰符续行形态");
                    // §8.4.1：全局函数/全局字段以裸 .method/.field 直接出现在
                    // 段内（段内一级缩进，类型体内成员为两级）
                    case BilMemberDeclaration member:
                        WriteMember(member, sb, Indent);
                        break;
                    default:
                        throw new CompilerInternalException($"未知的符号段条目类型: {entry.GetType().Name}");
                }
            }
            sb.Append("}\n");
        }

        private static void WriteTypeDeclaration(BilTypeDeclaration type, StringBuilder sb)
        {
            // §8.2：无 extends/implements 时单行；否则它们各占续行，
            // 修饰符与 { 收尾行
            var modifiers = string.Join(" ", type.Modifiers);
            if (type.ExtendsType == null && type.ImplementsTypes.Count == 0)
            {
                sb.Append($"{Indent}.type {type.Symbol} = {type.Kind}");
                if (modifiers.Length > 0) sb.Append($" {modifiers}");
                sb.Append(" {\n");
            }
            else
            {
                sb.Append($"{Indent}.type {type.Symbol} = {type.Kind}\n");
                if (type.ExtendsType != null)
                {
                    sb.Append($"{Indent}{Indent}extends {type.ExtendsType}\n");
                }
                if (type.ImplementsTypes.Count > 0)
                {
                    sb.Append($"{Indent}{Indent}implements {string.Join(", ", type.ImplementsTypes)}\n");
                }
                sb.Append($"{Indent}{Indent}");
                if (modifiers.Length > 0) sb.Append($"{modifiers} ");
                sb.Append("{\n");
            }
            foreach (var member in type.Members)
            {
                WriteMember(member, sb, Indent + Indent);
            }
            sb.Append($"{Indent}}}\n");
        }

        // 成员声明输出；indent 为成员行基础缩进（类型体内两级，§8.4.1 段内
        // 裸成员一级）
        private static void WriteMember(BilMemberDeclaration member, StringBuilder sb, string indent)
        {
            switch (member)
            {
                case BilSimpleMemberDeclaration simple:
                    if (simple.ModifiersOnNextLine)
                    {
                        // §19 wrapper 隐藏字段示例形态：符号与修饰符分两行
                        sb.Append($"{indent}{simple.Keyword} {simple.Symbol}\n");
                        sb.Append($"{indent}{Indent}{string.Join(" ", simple.Modifiers)}\n");
                    }
                    else
                    {
                        sb.Append($"{indent}{simple.Keyword} {simple.Symbol}");
                        if (simple.Modifiers.Count > 0)
                        {
                            sb.Append($" {string.Join(" ", simple.Modifiers)}");
                        }
                        sb.Append('\n');
                    }
                    break;
                case BilCaseDeclaration caseDecl:
                    var parameters = new StringBuilder();
                    for (int i = 0; i < caseDecl.Parameters.Count; i++)
                    {
                        if (i > 0) parameters.Append(",");
                        parameters.Append($"{caseDecl.Parameters[i].Name}:{caseDecl.Parameters[i].TypeRef}");
                    }
                    // §8.5：discriminant auto / discriminant res(R)
                    var discriminant = caseDecl.DiscriminantResource == null
                        ? "auto"
                        : $"res({caseDecl.DiscriminantResource})";
                    sb.Append($"{indent}.case {caseDecl.QualifiedName}({parameters}) discriminant {discriminant}\n");
                    break;
                default:
                    throw new CompilerInternalException($"未知的成员声明类型: {member.GetType().Name}");
            }
        }

        // ===== 函数（§9）=====

        private static void WriteFunction(BilFunction function, StringBuilder sb)
        {
            sb.Append($"fn({function.Symbol}) {{\n");

            sb.Append($"{Indent}.args {{\n");
            for (int i = 0; i < function.Args.Count; i++)
            {
                var trailing = i < function.Args.Count - 1 ? "," : "";
                sb.Append($"{Indent}{Indent}{function.Args[i].Name} = {function.Args[i].TypeRef}{trailing}\n");
            }
            sb.Append($"{Indent}}}\n");

            sb.Append('\n');
            sb.Append($"{Indent}.vars {{\n");
            for (int i = 0; i < function.Vars.Count; i++)
            {
                var trailing = i < function.Vars.Count - 1 ? "," : "";
                sb.Append($"{Indent}{Indent}{function.Vars[i].TypeRef} {function.Vars[i].Name}{trailing}\n");
            }
            sb.Append($"{Indent}}}\n");

            foreach (var block in function.Blocks)
            {
                sb.Append('\n');
                WriteBlock(block, sb);
            }
            sb.Append("}\n");
        }

        private static void WriteBlock(BilBlock block, StringBuilder sb)
        {
            sb.Append($"{Indent}.block {block.Id}");
            if (block.Modifiers.Count > 0)
            {
                sb.Append($" {string.Join(" ", block.Modifiers)}");
            }
            sb.Append(" {\n");
            foreach (var instruction in block.Instructions)
            {
                WriteInstruction(instruction, sb);
            }
            sb.Append($"{Indent}}}\n");
        }

        // ===== 指令（§10–§16）=====

        private static void WriteInstruction(BilInstruction instruction, StringBuilder sb)
        {
            switch (instruction.Opcode)
            {
                // §16.6 switch 规范排版：首行 selector + 常量表，其后 block 表 /
                // default / breakid 各占一行
                case "switch":
                    sb.Append($"{Indent}{Indent}switch {instruction.Operands[0].Render()} {instruction.Operands[1].Render()}\n");
                    for (int i = 2; i < instruction.Operands.Count; i++)
                    {
                        sb.Append($"{Indent}{Indent}{Indent}{instruction.Operands[i].Render()}\n");
                    }
                    break;
                // §16.7 try 规范排版：首行 try block，其后异常变量 / catch 表 /
                // finally 各占一行
                case "try":
                    sb.Append($"{Indent}{Indent}try {instruction.Operands[0].Render()}\n");
                    for (int i = 1; i < instruction.Operands.Count; i++)
                    {
                        sb.Append($"{Indent}{Indent}{Indent}{instruction.Operands[i].Render()}\n");
                    }
                    break;
                default:
                    sb.Append($"{Indent}{Indent}{instruction.Opcode}");
                    foreach (var operand in instruction.Operands)
                    {
                        sb.Append($" {operand.Render()}");
                    }
                    sb.Append('\n');
                    break;
            }
        }
    }
}
