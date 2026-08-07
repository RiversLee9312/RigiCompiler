using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LatteCompiler.Bil
{
    // BIL 文本生成器（BIL_STANDARD §4/§5.6/§20）：模型 → 标准 BIL 文本。
    // 只输出标准 spelling（不输出 legacy）；段物理顺序与 §4 一致且全部
    // 输出（空段也输出，§4）；排版严格对齐 §20 黄金示例（4 空格缩进、
    // 段间空行）；换行统一 \n，不随平台漂移。
    // M57 起指令/修饰符/种类拼写全部来自模型自渲染（BilInstruction.
    // WriteTo / BilModifier.Render / BilSpellings），本类只提供段落框架
    // 与缩进——不再有 opcode/种类/修饰符的字符串 switch。
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
                sb.Append($"{Indent}{entry.Key} = {BilSpellings.Of(entry.Type)} {entry.LiteralText}\n");
            }
            sb.Append("}\n");
        }

        private static void WriteResources(BilModule module, StringBuilder sb)
        {
            sb.Append('\n');
            sb.Append("Resources {\n");
            for (int i = 0; i < module.Resources.Count; i++)
            {
                // 条目后逗号（最后一项除外，§20 示例形态）
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
                    sb.Append($"{indent}{scalar.Name} = {BilSpellings.Of(scalar.Type)} {scalar.LiteralText}{trailing}\n");
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
                        // §19.2 规范排版：map 元素各占一行
                        sb.Append($"{indent}{collection.Name} = {collection.Header} {{\n");
                        for (int i = 0; i < collection.Elements.Count; i++)
                        {
                            var comma = i < collection.Elements.Count - 1 ? "," : "";
                            sb.Append($"{indent}{Indent}{collection.Elements[i]}{comma}\n");
                        }
                        sb.Append($"{indent}}}{trailing}\n");
                    }
                    break;
                // §19.4 switch-table：单行形态
                case BilSwitchTableResource switchTable:
                    sb.Append($"{indent}{switchTable.Name} = {switchTable.HeaderText} {{ {string.Join(", ", switchTable.Elements)} }}{trailing}\n");
                    break;
                // §19.5 catch-table：元素各占一行
                case BilCatchTableResource catchTable:
                    sb.Append($"{indent}{catchTable.Name} = catch-table {{\n");
                    for (int i = 0; i < catchTable.Entries.Count; i++)
                    {
                        var comma = i < catchTable.Entries.Count - 1 ? "," : "";
                        sb.Append($"{indent}{Indent}{catchTable.Entries[i].Render()}{comma}\n");
                    }
                    sb.Append($"{indent}}}{trailing}\n");
                    break;
                default:
                    throw new CompilerInternalException($"未知的资源类型: {resource.GetType().Name}");
            }
        }

        // ===== 符号声明（§8）=====

        private static void WriteSymbolSection(StringBuilder sb, string sectionName,
            List<BilSymbolSectionEntry> entries)
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
                    // （§20 wrapper 隐藏字段示例），生成器不产生该组合
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
            // 修饰符与 { 收尾行。generic(...)（S9e）紧跟 kind 同行
            var modifiers = RenderModifiers(type.Modifiers);
            var kind = BilSpellings.Of(type.Kind);
            var generic = type.GenericParameters.Count > 0
                ? " generic(" + string.Join(", ", type.GenericParameters.Select((name, i) =>
                    GenericParameterText(type, name, i))) + ")"
                : "";
            if (type.ExtendsType == null && type.ImplementsTypes.Count == 0)
            {
                sb.Append($"{Indent}.type {type.Symbol} = {kind}{generic}");
                if (modifiers.Length > 0) sb.Append($" {modifiers}");
                sb.Append(" {\n");
            }
            else
            {
                sb.Append($"{Indent}.type {type.Symbol} = {kind}{generic}\n");
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

        private static string GenericParameterText(BilTypeDeclaration type, string name, int index)
        {
            var variance = index < type.GenericVariances.Count
                ? type.GenericVariances[index]
                : BilGenericVariance.None;
            return variance switch
            {
                BilGenericVariance.Out => "out " + name,
                BilGenericVariance.In => "in " + name,
                _ => name,
            };
        }

        // 成员声明输出；indent 为成员行基础缩进（类型体内两级，§8.4.1 段内
        // 裸成员一级）
        private static void WriteMember(BilMemberDeclaration member, StringBuilder sb, string indent)
        {
            switch (member)
            {
                case BilSimpleMemberDeclaration simple:
                    var modifiers = RenderModifiers(simple.Modifiers);
                    if (simple.ModifiersOnNextLine)
                    {
                        // §20 wrapper 隐藏字段示例形态：符号与修饰符分两行
                        sb.Append($"{indent}{BilSpellings.Of(simple.Kind)} {simple.Symbol}\n");
                        sb.Append($"{indent}{Indent}{modifiers}\n");
                    }
                    else
                    {
                        sb.Append($"{indent}{BilSpellings.Of(simple.Kind)} {simple.Symbol}");
                        if (modifiers.Length > 0)
                        {
                            sb.Append($" {modifiers}");
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

        private static string RenderModifiers(IReadOnlyList<BilModifier> modifiers)
        {
            var parts = new List<string>();
            foreach (var modifier in modifiers)
            {
                parts.Add(modifier.Render());
            }
            return string.Join(" ", parts);
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
                var modifierParts = new List<string>();
                foreach (var modifier in block.Modifiers)
                {
                    modifierParts.Add(BilSpellings.Of(modifier));
                }
                sb.Append($" {string.Join(" ", modifierParts)}");
            }
            sb.Append(" {\n");
            foreach (var instruction in block.Instructions)
            {
                // 指令自渲染（§10–§16；多行形态由指令的
                // FirstLineOperandCount 声明）
                instruction.WriteTo(sb, Indent + Indent);
            }
            sb.Append($"{Indent}}}\n");
        }
    }
}
