import core.serialization.*
// expect-output: parcel-unicode-key-ok
// expect-exit: 0
// §20.6：业务字段键与源码字段名同口径；拒绝路径不得写入业务表。
@Serializable
class UnicodeFields {
    pub var 字段: String = "汉字"
    pub var αβ: String = "希腊"
    pub var a١: String = "数字后缀"
}

pub func main(): i32 {
    const p = new Parcel("UnicodeFields")
    var rejected = false
    try { p.setElement\<String>("😀", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 1 }

    rejected = false
    try { p.setDynamic("。", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 2 }

    rejected = false
    try { p.setElement\<String>("١abc", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 3 }

    rejected = false
    try { p.setDynamic("á", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 4 }

    rejected = false
    try { p.setElement\<String>("a𐐀", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 9 }

    // Unicode 17 新增但当前 .NET 10 的词法快照未接纳的八个 BMP 字母。
    rejected = false
    try { p.setDynamic("a࢏", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 10 }
    rejected = false
    try { p.setDynamic("a౜", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 11 }
    rejected = false
    try { p.setDynamic("a೜", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 12 }
    rejected = false
    try { p.setDynamic("a꟎", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 13 }
    rejected = false
    try { p.setDynamic("a꟏", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 14 }
    rejected = false
    try { p.setDynamic("a꟒", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 15 }
    rejected = false
    try { p.setDynamic("a꟔", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 16 }
    rejected = false
    try { p.setDynamic("a꟱", "x") }
    catch (e: core.IllegalArgumentException) { rejected = true }
    if ((not rejected) or (p.elementCount() != 0L)) { return 17 }

    p.setElement\<String>("字段", "汉字")
    p.setDynamic("αβ", "希腊")
    p.setElement\<String>("a١", "数字后缀")
    if ((p.getElement\<String>("字段") if? "") != "汉字") { return 5 }
    if ((p.getElement\<String>("αβ") if? "") != "希腊") { return 6 }
    if ((p.getElement\<String>("a١") if? "") != "数字后缀") { return 7 }
    if (p.elementCount() != 3L) { return 8 }
    core.io.Console.println("parcel-unicode-key-ok")
    return 0
}
