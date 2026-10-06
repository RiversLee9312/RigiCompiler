using System.Security.Cryptography;
using System.Text;
using RigiCompiler.Tests;

namespace RigiCompiler.TUnitTests;

// 迁移前 inventory 的逐 suite 标签摘要，独立于新目录；确认旧覆盖未被自我镜像掩盖。
// 这里只排除从原方法组抽出的 7 个动作，原组标签及有效断言继续保留。
internal static class MigrationCoverageBaseline
{
    private static readonly HashSet<string> Extracted = new(StringComparer.Ordinal)
    { "lexer.slash", "lexer.multiline-comment", "parser.add", "parser.no-precedence",
        "bil.reader.scalar-roundtrip", "bil.verifier.reserved-this", "native.hello-world-bil" };
    private static readonly IReadOnlyDictionary<string, (int Count, string Digest)> Original =
        new Dictionary<string, (int, string)>(StringComparer.Ordinal)
    {
        ["Literal"] = (19, "086f252a73258f9389f4dcf9c72a0d4bb704a4a26ca36ee15e81f6c94544d60a"),
        ["TypeReference"] = (6, "8ef5d1ca42a47ecf732a69278b71bb6f137eb2117cebc6649d503befb6782336"),
        ["VariableDeclaration"] = (6, "f6101bbedbf6ba39ad3478ba61fd4f2c30153e17e7e1533a106621bd303a2ac3"),
        ["Expression"] = (24, "8223efeac5124593addc992afd6724202aa4509c94d6868f252f01a85e7e5b08"),
        ["GenericParsing"] = (6, "03831927e0fe606c593d1f8eca72ad7c14c71c6230f0844b532e26a0c9d1e77c"),
        ["GenericParameters"] = (5, "62c6cb8c4e3a320e5e60238cb8a2728472536dae1db99ee5d30e3fb174900eaa"),
        ["ParameterList"] = (8, "3349bdab7280cffca3d9fd2ff22570e3a2db9bbf21d88be95e101c28dbf1ceff"),
        ["Lambda"] = (11, "c2f52ef1ed6a1c629fc3fec5eeaf838e409d88e7a207dd98c42b69bba1a1875b"),
        ["IfExpression"] = (7, "6829ef760ad96faa9d4e0c7b03e50d527a89e33ea0e1a6090d145f0c1406a4f5"),
        ["SwitchExpression"] = (7, "1cbcf4867172b751466ec9240d2cc9a9e1031fbdc9e7a9f018c030092ca34c82"),
        ["TypeOf"] = (6, "491fc6e8491b186a4748e1675d63c0175beae4b2f142af5bb0bc7204a74de543"),
        ["CodeBlock"] = (8, "94ab5a4707d544d12dfa70a069971af9bc162ae566647047bc3fc2818d1f8eb2"),
        ["Loop"] = (7, "f0b778bcad3e06e417be3405da111b21c3f2a72c5d296b084f754b4d2f0955f3"),
        ["TryCatchFinally"] = (7, "86e39ae53480809a9a597340a38f6b8f44ab7b78302a410fb616a0733777460e"),
        ["SeqBlock"] = (7, "2fb816915a76aedbaec2a86b5f0ff6016d290a0e84da35467eb248dee9c52fa2"),
        ["Throw"] = (5, "68fa50d33e55552a3c5991551b2e284372140bdecf28b366441c661117d60535"),
        ["CoroutineOps"] = (4, "e9adbb2644a45feab81a3d06515cec637ecfc7c529fb7857e8713535ef37c4cd"),
        ["TypeDeclaration"] = (16, "554ecd99464d0b7b318d6cdd623532300d5096ed3e4dd208ff80a7e1964c64a7"),
        ["PropertyAccessor"] = (4, "8dc6a899b9b8a558850f953ed18f292d8e1b3dba09ccdb57d5ac9304403c1ebb"),
        ["Import"] = (4, "782b96170929acd680b9079ad9396003f65aedfae702c601de9da40f955e906c"),
        ["Namespace"] = (3, "7139e5234906bb5a27380274f45249e2a9f8331822742248722fc3cba4d93a72"),
        ["TokenDisposition"] = (4, "c1934b868b0b6f5964d2e91dde2fd8de0385ba8a495621844a47a35f90d098a6"),
        ["ASTIntegrityValidator"] = (3, "925955b1b871a3506333ec9ddb8ebc810bdfad555e3e46e9352a515e9de75135"),
        ["LexerFuzz"] = (6005, "b51b1d2ad14fdde593d0da02132e2fa0111c6da1d7f3af3bb59660dff3eba693"),
        ["Logger"] = (3, "9d9ae00feb955502643cf3af0ee25faaedafb39754472e8a35d1068b0d92bce5"),
        ["AstJsonlSerializer"] = (8, "6a51c6959b07b370f252817c139a94b5525bb37284e51234e18670f27ee02341"),
        ["CommandLineParser"] = (10, "ac3f964056b29ecab3c86fbf1073446a2a10728996da8d5f0064615d8bfcf9b8"),
        ["Path"] = (4, "2f3230183e0c830abe997309419ce33d9319a7d7d584dd3dc5b3438fa595ee11"),
        ["ArgumentList"] = (5, "3fafa5acb2fff5f841fd74367465c69f839db9ae7e766b3e13a6346cc280b9ca"),
        ["MultilineString"] = (6, "c33c1cc3745c84f50fe296608c634510954e76bb1498ad038869b1e1613caeca"),
        ["Diagnostics"] = (2, "dee82153a239502246c3d80aba5045a570c03859b309ff8d17d02f3d546dbfa2"),
        ["SymbolGraph"] = (7, "88633bf8998c042cbb166f7cff978aa303ebc41c979db11d09771a369b3e17db"),
        ["CanonicalSymbolPrinter"] = (11, "fe94efb8e30d763e76a82088a155313654b39f93278a4606619593f6584e99b9"),
        ["BilWriter"] = (7, "8501e516ef9cc791cd97bf88088cd9996fc718ae58700b0e466047cb814cf91e"),
        ["BilVerifier"] = (44, "1b6c1cf13b876a61c0dac384cb7fc1edc480594faf0e51f7c8bd1778048e30fd"),
        ["DeclarationCollector"] = (11, "c7fdf03011a76e03cb00db5cb2eaecd27ece8da1557ca7b6ca2489ea5961444e"),
        ["DeclarationResolver"] = (53, "d5708847e971535d7675e0454df8cc597dac4d8af332e4beb3767c1f322fd647"),
        ["Binder"] = (130, "5a097bd6ef2f3c1d5ef03c5c70c30e74046e972791541a26dc99961a58bfeff4"),
        ["StdlibSources"] = (21, "9f8f8d8a64b2aba35fdc6ae1d48c2c0c295dc03b270ea6095277033ee38a4ea9"),
        ["BilEmitter"] = (144, "192f5b10808dfb50d256f6c003de008e20afa26f8e54dfc0036543187dbb77e3"),
        ["Lowerer"] = (57, "aa4e97d60f4b2138c67518dcc290d367a8a6615d710bce3f45eb857ddf8a0332"),
        ["SmartCast"] = (13, "7d71d12cad30b9689db9a0338b8025aa0d29f8f9f8622c33b62efc470cb19dc9"),
        ["DispatchExplainer"] = (5, "cbe5c72cef2b3dd108d0d660cf163e3c2a9157009198b519c3659b8f662f60b6"),
        ["BilVm"] = (164, "d49b07abecd468c2f8b60a4cb8b33239a38dd70467194766d4798e7d102c37ff"),
        ["BilVmDispatch"] = (49, "4fe24a691e09e9b83dcb87294a24ef49bbbc7fab8fb8ae45b337010b982fb530"),
        ["BilVmWakeup"] = (10, "48d9c6bfdb7c472680fa876f9c2a9c3efe4495d86cb69b3323e3c66bd1eb33e6"),
        ["VmPrimitive"] = (8, "ee4150230b557719abae31435b6d4a7f0518baf8b8b50b3ecff4764e901bce57"),
        ["BilReader"] = (10, "c5e5941cc49aaf43810f607e6ac2981fe27a0db6b574f8bc1f2176a81cb890cc"),
        ["BilVmStress"] = (40, "e1c642d06218cceeb8da01d568e292331cffb2631bbe95fde2f2bb7faf431fb0"),
        ["EscapingSeqExpr"] = (4, "c42070e0f2d84b59c7bdc4a59567a45ce93bbe02854c29fc9fbbac056a85e280"),
        ["EscapingValueBlock"] = (18, "8d20a01449863e8d01af42c0a8ec6cb0edb236856d0b9698d7232f5419788a81"),
        ["SeqRouteHint"] = (15, "1b05e843f6ca072517b6ecaec940d08c394ac4878e3d5f8705ee5c638d9c619b"),
        ["EscapingSeqPosition"] = (15, "d3b4c6ebee41195771dd44a16762eb7df1a4f5522eea95430c4af09a8241f02a"),
        ["E2e"] = (272, "28f37a8309124f08428e45f4ee0b898c3e32f4c38bd231266ccb80fbed4e4674"),
        ["Middleware"] = (80, "c65f82b169e04f68d4d0bed416ea1d7a3fac66f8f753486bf2039bbe6928faf0"),
        ["NativeE2E"] = (605, "d2fd5fb1f8f46080660223151bea877762c5fb159f09a54ec6529127b8af3fa3"),
        ["BilVmTask"] = (25, "1acf601933a1fd2fbd560315b74f0cfef9b0db96aa208c1db1f0b20a7923c758"),
        ["NativeE2EArgs"] = (6, "1f568ca60cc2db11a42bc1f912795807762251883a4cbf4b01ba25653ce07330"),
        ["VmFsNoReplace"] = (12, "fc06b824d542d082df179ef4e9224596a555bf2581089a84703708b553152f03"),
        ["VmFsDanglingDelete"] = (2, "ad4f6437548384d86b66a3ccd66991ba5ec3eb47242038e36539ef0e63b8a6c6"),
        ["VmFsJunctionDelete"] = (3, "91e6740450c814aa3727eb93ad6465ec1c452b0a32f09b1264f27a3c4c22707b"),
        ["VmFsIdentity"] = (6, "dc30e73b9b93207d66f5a35c7f70c4f37c94207820f2c7cddf240ecc15af0928"),
        ["VmFsBirthTime"] = (2, "3725fcd0fdf71db942de281e8a3aab4d05866ed16d01f59ad0c4ebc8bc5dbbca"),
        ["VmFsDirOpen"] = (5, "428b666b1145f3f611ecac154cc4dc1ae5a4430f1f30f08f17ed41f322f316fa"),
        ["VmFsRealpath"] = (4, "9cb00b20cfe64a8e46819d9387c32d6e357f63fcd08575a59eefc637c195926b"),
        ["PerformanceMetrics"] = (6, "406f8447287b5917e2eef102c9e16c1a6a7cae9500200d9989a4c9cec78dced8"),
        ["CompilerParallel"] = (4, "b37e7fc3b5b63786a249c1c2441c0cc519e9bec7049130ceebfbd2490671729b"),
        ["Module"] = (29, "8573b229e673558e3a47d879489e4fe345204d8de3107a878ebd753eb7618ae1"),
    };
    internal static void Verify()
    {
        if (TestSuiteCatalog.Count != 70) throw new InvalidOperationException("迁移不得漏掉原 70 suites");
        foreach (var (suite, baseline) in Original)
        {
            var labels = TestInventory.Cases(suite).Select(item => item.Label).Where(label => !Extracted.Contains(label))
                .Order(StringComparer.Ordinal).ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', labels)))).ToLowerInvariant();
            if (labels.Length != baseline.Count || digest != baseline.Digest)
                throw new InvalidOperationException("迁移前标签摘要不一致：" + suite);
        }
        foreach (var (suite, variable, fallback) in new[] { ("SemanticsFuzz", "RIGI_SEMFUZZ_CASES", 3000),
            ("StressFuzz", "RIGI_STRESSFUZZ_CASES", Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 600 : 3000) })
        {
            var labels = TestInventory.Cases(suite).Select(item => item.Label).ToArray();
            if (!labels.SequenceEqual(Enumerable.Range(0, TestInventory.Budget(variable, fallback)).Select(index => "seed-case-" + index)))
                throw new InvalidOperationException("可配置 fuzz 预算和全局 seed 覆盖不一致：" + suite);
        }
        foreach (var id in new[] { "lexer.slash", "lexer.multiline-comment", "parser.add", "parser.no-precedence",
            "semantic.wrapper-this-return-negative", "bil.reader.scalar-roundtrip", "bil.verifier.reserved-this", "vm.hello-world", "native.hello-world-bil" })
            if (CaseCatalog.All.Count(item => item.Id == id) != 1) throw new InvalidOperationException("pilot 稳定 ID 必须只发现一次：" + id);
    }
}
