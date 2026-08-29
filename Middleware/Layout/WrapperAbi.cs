namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// wrapper 隐藏存储 ABI（BIL §5.3）：字段名 <c>#.wrapper.&lt;全名&gt;</c>，
    /// 不出现在 BIL 文本。Entity / 字段-Value / Method 三类槽位。
    /// </summary>
    public static class WrapperAbi
    {
        public const string HiddenFieldInfix = "#.wrapper.";
        public const string FieldKindInfix = "#.wrapper.field.";
        public const string MethodKindInfix = "#.wrapper.method.";

        // Entity：Host#.wrapper.W@W
        public static string EntityFieldSymbol(string hostCanonical, string wrapperCanonical) =>
            hostCanonical + HiddenFieldInfix + wrapperCanonical + "@" + wrapperCanonical;

        // 字段-Value：Host#.wrapper.field.<字段 canonical>.W@W（末 @ 分隔类型）
        public static string FieldValueSymbol(string hostCanonical, string fieldCanonical,
            string wrapperCanonical) =>
            hostCanonical + FieldKindInfix + fieldCanonical + "." + wrapperCanonical
            + "@" + wrapperCanonical;

        // Method：Host#.wrapper.method.<方法 canonical>.W@W
        public static string MethodFieldSymbol(string hostCanonical, string methodCanonical,
            string wrapperCanonical) =>
            hostCanonical + MethodKindInfix + methodCanonical + "." + wrapperCanonical
            + "@" + wrapperCanonical;

        public const string HostFieldInfix = "#.host@";

        // wrapper 实例内的宿主回指（不进 refMap：借用指针，get.self 时 +1）
        public static string HostFieldSymbol(string wrapperPlanKey) =>
            wrapperPlanKey + HostFieldInfix + ".any";

        public static bool IsHostField(string fieldSymbol) =>
            fieldSymbol.Contains(HostFieldInfix, System.StringComparison.Ordinal);
    }
}
