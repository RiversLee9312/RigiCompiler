using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// BIL 测试基建（M58）：BilVerifier 断言 + 中端全管线驱动 + 规范化
    /// 形状黄金。BIL 相关测试统一以此表达：
    ///   CheckNoErrors（语义零诊断，各套件自有）→ CheckBilValid（验证器
    ///   零错误）→ CheckFnShape/CheckResShape（发射选择回归锁）。
    ///
    /// 形状黄金：沿用原 BilEmitterTests 私有 RenderFn/RenderFnAllBlocks/
    /// RenderResources 的文本风格，但资源名按首次出现顺序重编号为
    /// res(#0)/res(#1)……——消除 stdlib 基线资源（R_0..R_4）偏移这一
    /// 最脆弱点；指令序列/操作数/.tN 编号/block id 仍逐字节锁定。
    /// lambda 对象模型（SYNTAX §5.2）：隐藏类名含编译期 UUID，比较前
    /// 统一归一化为 `..lambda..UUID`。
    /// </summary>
    public static class BilTestHarness
    {
        // 用户源文件名（Origin 链断言 sourceName 用）
        public const string UserSourceName = "hello.rg";

        // lambda 隐藏类名 UUID 归一化（Guid "N" = 32 位十六进制）
        private static readonly Regex LambdaUuidPattern = new Regex(
            @"\.\.lambda\.\.[0-9a-fA-F]{32}", RegexOptions.Compiled);

        // cell 隐藏子类名 UUID 归一化（统一 cell 存储：逐变量合成，Guid "N"）
        private static readonly Regex CellUuidPattern = new Regex(
            @"\.\.cell\.\.[0-9a-fA-F]{32}", RegexOptions.Compiled);

        public static string NormalizeLambdaUuids(string text) =>
            CellUuidPattern.Replace(
                LambdaUuidPattern.Replace(text, "..lambda..UUID"), "..cell..UUID");

        // ===== 中端全管线驱动（自 BilEmitterTests 提升共享）=====
        // stdlib（在前）+ 用户源组 CompilationUnit → P1 → P2 → P3 → P4a
        // → P4b → BilWriter 文本（与 CLI RunSemanticPipeline 同序）
        public static (CompilationUnit Unit, BilModule Module, string Text) EmitBilUnit(
            string userSource, string moduleName = "hello")
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.Add(TestHarness.ParseRoot(userSource, UserSourceName));
            var unit = new CompilationUnit(roots.ToArray());
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            var bodies = Binder.Bind(unit, declarations);
            var lowered = Lowerer.Lower(unit, bodies);
            var module = BilEmitter.Emit(unit, lowered, moduleName);
            return (unit, module, NormalizeLambdaUuids(BilWriter.Write(module)));
        }

        // §17 命名空间切分形态：与 EmitBilUnit 同管线，产出 merged + 切片
        public static (CompilationUnit Unit, BilEmitResult Result) EmitBilSlices(
            string userSource, string moduleName = "hello")
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.Add(TestHarness.ParseRoot(userSource, UserSourceName));
            var unit = new CompilationUnit(roots.ToArray());
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            var bodies = Binder.Bind(unit, declarations);
            var lowered = Lowerer.Lower(unit, bodies);
            return (unit, BilEmitter.EmitWithSlices(unit, lowered, moduleName));
        }

        // ===== 验证器断言 =====

        // 期望验证器零错误（合法产出）
        public static void CheckBilValid(string label, BilModule module)
        {
            var errors = BilVerifier.Verify(module);
            TestHarness.CheckTrue(label, errors.Count == 0,
                string.Join("; ", errors.Select(e => e.ToString())));
        }

        // 期望验证器报错且消息含片段（负例）
        public static void CheckBilInvalid(string label, BilModule module, string expectedMessagePart)
        {
            var errors = BilVerifier.Verify(module);
            foreach (var error in errors)
            {
                if (error.Message.Contains(expectedMessagePart))
                {
                    TestHarness.CheckTrue(label, true);
                    return;
                }
            }
            TestHarness.CheckTrue(label, false,
                errors.Count == 0
                    ? "验证器未报告任何错误"
                    : "缺少预期错误；实际: " + string.Join("; ", errors.Select(e => e.ToString())));
        }

        // ===== 规范化形状黄金 =====

        // fn 形状：.vars + 全部 block 指令（单 block 省略 .block 头尾，
        // 与原 RenderFn/RenderFnAllBlocks 双形态一致）；资源名重编号 res(#k)
        public static void CheckFnShape(string label, BilModule module, string fnSymbol,
            string expected)
        {
            // FirstOrDefault + 断言存在性：Single 在符号不匹配时抛
            // InvalidOperationException 中断整个套件，而非记 FAIL
            var function = module.Functions.FirstOrDefault(f => f.Symbol == fnSymbol);
            if (function == null)
            {
                TestHarness.CheckTrue(label, false,
                    $"模块中找不到 fn {fnSymbol}（实际: {string.Join(", ", module.Functions.Select(f => f.Symbol))}）");
                return;
            }
            TestHarness.Check(label, NormalizeLambdaUuids(RenderFnShape(function)),
                NormalizeLambdaUuids(expected));
        }

        // 资源段形状：每资源一行（原名重编号为 #k）
        public static void CheckResShape(string label, BilModule module, string expected)
        {
            var resourceIds = new Dictionary<string, int>();
            var sb = new StringBuilder();
            foreach (var resource in module.Resources)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append('#').Append(ResourceIdOf(resourceIds, resource));
                sb.Append(" = ").Append(RenderResourceBody(resource));
            }
            TestHarness.Check(label, sb.ToString(), expected);
        }

        private static string RenderFnShape(BilFunction function)
        {
            var resourceIds = new Dictionary<string, int>();
            var sb = new StringBuilder();
            sb.Append(".vars { ");
            sb.Append(string.Join(", ", function.Vars.Select(v => $"{v.TypeRef} {v.Name}")));
            sb.Append(" }\n");
            var singleBlock = function.Blocks.Count == 1;
            foreach (var block in function.Blocks)
            {
                if (!singleBlock)
                {
                    sb.Append($".block {block.Id}");
                    if (block.Modifiers.Count > 0)
                    {
                        sb.Append(" " + string.Join(" ", block.Modifiers.Select(BilSpellings.Of)));
                    }
                    sb.Append(" {\n");
                }
                foreach (var instruction in block.Instructions)
                {
                    sb.Append(instruction.Opcode);
                    foreach (var operand in instruction.Operands)
                    {
                        sb.Append(" " + RenderOperand(operand, resourceIds));
                    }
                    sb.Append('\n');
                }
                if (!singleBlock)
                {
                    sb.Append("}\n");
                }
            }
            return sb.ToString();
        }

        private static string RenderOperand(BilOperand operand, Dictionary<string, int> resourceIds)
        {
            switch (operand)
            {
                case BilResourceOperand resourceOperand:
                    return $"res(#{ResourceIdOf(resourceIds, resourceOperand.Resource)})";
                case BilOperandList list:
                    return "[" + string.Join(", ",
                        list.Items.Select(item => RenderOperand(item, resourceIds))) + "]";
                default:
                    return operand.Render();
            }
        }

        // 资源体渲染（与原 RenderResources 一致；catch-table 多行排版只由
        // BilWriter 保证，此处单行形态）
        private static string RenderResourceBody(BilResource resource)
        {
            return resource switch
            {
                BilScalarResource s => $"{BilSpellings.Of(s.Type)} {s.LiteralText}",
                BilNullResource n => $"null type({n.TypeRef})",
                BilCollectionResource c =>
                    $"{c.Header} {{ {string.Join(", ", c.Elements)} }}",
                BilSwitchTableResource t =>
                    $"{t.HeaderText} {{ {string.Join(", ", t.Elements)} }}",
                BilCatchTableResource ct =>
                    $"catch-table {{ {string.Join(", ", ct.Entries.Select(e => e.Render()))} }}",
                _ => $"<{resource.GetType().Name}>",
            };
        }

        // 资源重编号：按首次出现顺序分配 #0/#1……
        private static int ResourceIdOf(Dictionary<string, int> resourceIds, BilResource resource)
        {
            if (!resourceIds.TryGetValue(resource.Name, out var id))
            {
                id = resourceIds.Count;
                resourceIds.Add(resource.Name, id);
            }
            return id;
        }
    }
}
