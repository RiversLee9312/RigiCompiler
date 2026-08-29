namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// TypeSheet / TypeInfo 字段序（与 <c>rigi_rt/arc.h</c> <c>RigiTypeSheet</c> /
    /// <c>RigiTypeInfo</c> 逐位镜像）。LLVM GEP 下标与 C 结构偏移共用本表；
    /// Emit 只填函数指针与常量，不得另开一套序。
    /// 数组前缀尺寸/偏移见 <see cref="TypeLayout.ArrayPrefixSize"/>。
    /// </summary>
    public static class TypeSheetAbi
    {
        // RigiTypeSheet 字段序（arc.h / TypeSheetEmitter.SheetStructType 互指）
        public const int FieldTypeInfoId = 0;
        public const int FieldBaseTypeId = 1;
        public const int FieldTypeSize = 2;
        public const int FieldTypeFlags = 3;
        public const int FieldVTableSize = 4;
        public const int FieldVTable = 5;
        public const int FieldIMapSize = 6;
        public const int FieldIMap = 7;
        public const int FieldRefMapSize = 8;
        public const int FieldRefMap = 9;
        public const int SheetFieldCount = 10;

        // RigiTypeInfo 字段序（TypeInfoEmitter.InfoStructType 互指）
        public const int InfoFieldName = 0;
        public const int InfoFieldSheet = 1;
        public const int InfoFieldWrappers = 2;
        public const int InfoFieldWrapperCount = 3;
        public const int InfoFieldIfaceClosure = 4;
        public const int InfoFieldIfaceClosureCount = 5;
        public const int InfoFieldCount = 6;
    }
}
