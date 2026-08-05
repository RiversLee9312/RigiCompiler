using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BIL 验证器（M58，BIL_STANDARD §21；提前自路线图 S12）：消费 BilModule
    // 对象模型，检查 frontend 产出是否满足 §21 的合法性约束，输出结构化
    // 错误列表（空列表即合法）。只依赖 Bil/ 目录，不引用 Semantic/AST。
    //
    // 模型层已保证的免检项（M57 强类型化）：opcode 拼写/操作数个数与类别
    // 由构造签名固定；blk/res 操作数持对象引用，悬空引用不可构造。
    // 验证器主战场是模型无法表达的检查：对象引用的成员资格（引用的
    // block/resource 必须属于当前 fn/本 module）、变量身份与类型（字符串名
    // → 声明与类型环境）、控制流结构、definite assignment、.breakid
    // capability、声明侧符号规则。
    //
    // 检查按 §21 类别分文件（partial class）：
    //   BilVerifier.cs          —— 入口 + §21.1 词法与语法
    //   BilVerifier.Symbols.cs  —— §21.2 符号 + §21.7 泛型参数包 + §21.8 声明侧
    //   BilVerifier.Types.cs    —— §21.3 类型（逐指令 switch）
    //   BilVerifier.Flow.cs     —— §21.4 DA + §21.5 控制流 + §21.6 breakid
    // §21.9（VM 可执行性）是 VM 语义要求，非静态可判，不在静态验证范围。
    //
    // 防误报降级原则：验证器宁可漏报不可误报——含 .generic< 的 typeid 位置
    // 表达式不做严格匹配；类型声明查不到（external 不完整）时跳过派生规则。

    // 验证错误：Rule 取 § 号（如 "21.5"），Context 定位（fn 符号 / block id /
    // 符号名），Message 描述违规内容
    public sealed class BilVerificationError
    {
        public string Rule { get; }
        public string Context { get; }
        public string Message { get; }

        public BilVerificationError(string rule, string context, string message)
        {
            Rule = rule;
            Context = context;
            Message = message;
        }

        public override string ToString() => $"§{Rule} [{Context}] {Message}";
    }

    public static partial class BilVerifier
    {
        public static IReadOnlyList<BilVerificationError> Verify(BilModule module)
        {
            var errors = new List<BilVerificationError>();
            var context = new BilVerificationContext(module);
            VerifyLexicalAndStructure(module, errors);
            VerifyDeclarations(context, errors);
            foreach (var function in module.Functions)
            {
                var functionContext = new BilFunctionContext(context, function);
                VerifyFunctionSignature(functionContext, errors);
                VerifyFunctionTypes(functionContext, errors);
                VerifyFunctionFlow(functionContext, errors);
            }
            return errors;
        }

        // 指令引用的全部 block（含 try 的 catch-table 条目 handler——表资源
        // 是模块级的，块成员资格只能在引用它的 fn 上下文里检查）；供函数级
        // 指令枚举（BilFunctionContext）与控制流检查（Flow）共用
        internal static IEnumerable<BilBlock> ReferencedBlocks(BilInstruction instruction)
        {
            switch (instruction)
            {
                case IfInstruction ifInstruction:
                    yield return ifInstruction.ThenBlock;
                    if (ifInstruction.ElseBlock != null)
                    {
                        yield return ifInstruction.ElseBlock;
                    }
                    break;
                case LoopInstruction loop:
                    yield return loop.Body;
                    if (loop.EnumBlock != null)
                    {
                        yield return loop.EnumBlock;
                    }
                    yield return loop.Judge;
                    break;
                case SwitchInstruction switchInstruction:
                    foreach (var itemBlock in switchInstruction.ItemBlocks)
                    {
                        yield return itemBlock;
                    }
                    yield return switchInstruction.DefaultBlock;
                    break;
                case CallBlockInstruction call:
                    yield return call.Block;
                    break;
                case TryInstruction tryInstruction:
                    yield return tryInstruction.Body;
                    if (tryInstruction.CatchTable is BilCatchTableResource catchTable)
                    {
                        foreach (var entry in catchTable.Entries)
                        {
                            yield return entry.Handler;
                        }
                    }
                    if (tryInstruction.FinallyBlock != null)
                    {
                        yield return tryInstruction.FinallyBlock;
                    }
                    break;
            }
        }

        // §5.1 本地标识符字符集（资源名 / block id / 变量名）：字符限于
        // [A-Za-z0-9_-]；用户标识符不得以 . 开头，但编译器合成名/保留名
        // （.return/.this/.tN/.sN/.bN……）带前导点——验证器无法区分用户名与
        // 合成名，统一允许至多一个前导点。
        // S9e：§7.1 泛型/可变参数保留名家族（.generic.T / .vargs.args /
        // .kwargs.args——§5.1 保留名，含内部点）额外放行
        internal static bool IsLocalIdentifier(string name)
        {
            if (name.StartsWith(".generic.") || name.StartsWith(".vargs.")
                || name.StartsWith(".kwargs."))
            {
                return true;
            }
            var body = name.StartsWith(".") ? name.Substring(1) : name;
            if (body.Length == 0)
            {
                return false;
            }
            foreach (var c in body)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                {
                    return false;
                }
            }
            return true;
        }

        // ===== §21.1 词法与语法验证 =====
        // （段结构与操作数形态由模型构造保证；"用户标识符不得以 . 开头"
        // 无法与合成变量 .tN/.sN/.bN 区分，不查）
        private static void VerifyLexicalAndStructure(BilModule module, List<BilVerificationError> errors)
        {
            // 版本号受支持（§4）
            if (module.BilVersion != "1.1")
            {
                errors.Add(new BilVerificationError("21.1", "module",
                    $"不支持的 BIL 版本 \"{module.BilVersion}\""));
            }

            // Metadata 键非空（§4.1）
            foreach (var entry in module.Metadata)
            {
                if (entry.Key.Length == 0)
                {
                    errors.Add(new BilVerificationError("21.1", "Metadata", "Metadata 键为空"));
                }
            }

            // 资源名合法且唯一
            var resourceNames = new HashSet<string>();
            foreach (var resource in module.Resources)
            {
                if (!IsLocalIdentifier(resource.Name))
                {
                    errors.Add(new BilVerificationError("21.1", "Resources",
                        $"非法资源名 \"{resource.Name}\""));
                }
                if (!resourceNames.Add(resource.Name))
                {
                    errors.Add(new BilVerificationError("21.1", "Resources",
                        $"资源名重复 \"{resource.Name}\""));
                }
            }

            foreach (var function in module.Functions)
            {
                var context = function.Symbol;
                VerifyFunctionLexical(function, context, errors);
            }
        }

        private static void VerifyFunctionLexical(BilFunction function, string context,
            List<BilVerificationError> errors)
        {
            // .args/.vars 变量名合法、唯一（参数与局部变量共用 $name 引用空间）
            var variableNames = new HashSet<string>();
            var returnCount = 0;
            var thisCount = 0;
            for (var i = 0; i < function.Args.Count; i++)
            {
                var arg = function.Args[i];
                if (!IsLocalIdentifier(arg.Name))
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"非法参数名 \"{arg.Name}\""));
                }
                if (!variableNames.Add(arg.Name))
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"变量名重复 \"{arg.Name}\""));
                }
                if (arg.Name == ".return")
                {
                    returnCount++;
                    if (i != 0)
                    {
                        errors.Add(new BilVerificationError("21.1", context,
                            ".return 必须是 .args 首条"));
                    }
                }
                if (arg.Name == ".this")
                {
                    thisCount++;
                }
                // §6.2：.void 只能用作无结果方法的返回类型
                if (arg.Name != ".return" && arg.TypeRef == ".void")
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"参数 \"{arg.Name}\" 不得以 .void 为类型"));
                }
            }
            if (returnCount != 1)
            {
                errors.Add(new BilVerificationError("21.1", context,
                    $".args 必须恰好一个 .return（实际 {returnCount}）"));
            }
            if (thisCount > 1)
            {
                errors.Add(new BilVerificationError("21.1", context,
                    $".args 至多一个 .this（实际 {thisCount}）"));
            }
            foreach (var variable in function.Vars)
            {
                if (!IsLocalIdentifier(variable.Name))
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"非法局部变量名 \"{variable.Name}\""));
                }
                if (!variableNames.Add(variable.Name))
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"变量名重复 \"{variable.Name}\""));
                }
                // 保留名（§7 隐藏参数名）不得作为局部变量声明
                if (variable.Name == ".return" || variable.Name == ".this"
                    || variable.Name.StartsWith(".generic.")
                    || variable.Name.StartsWith(".vargs.")
                    || variable.Name.StartsWith(".kwargs."))
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"保留名 \"{variable.Name}\" 不得声明为局部变量"));
                }
                if (variable.TypeRef == ".void")
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"局部变量 \"{variable.Name}\" 不得以 .void 为类型"));
                }
            }

            // block id 合法且唯一（§9.4）
            var blockIds = new HashSet<string>();
            foreach (var block in function.Blocks)
            {
                if (!IsLocalIdentifier(block.Id))
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"非法 block id \"{block.Id}\""));
                }
                if (!blockIds.Add(block.Id))
                {
                    errors.Add(new BilVerificationError("21.1", context,
                        $"block id 重复 \"{block.Id}\""));
                }
            }
        }
    }
}
