import core.serialization.*
import core.collections.*
// expect-output: reflection-fields-ok
// expect-exit: 0
// 块 5-1a 通用字段反射（§4.6.3「反射与实现边界」）+ 块 5-1b native 面对拍
//   与 typeNameOf\<T\>() 无参重载：
//   字段清单 = 应序列化字段闭包（含继承字段；@Temporary / static 排除；
//   可空标志结构化、.nullable 包装不进 typeName 文本）；泛型 class 闭合
//   实参代入（Holder\<i32\> 字段类型是 .i32 而非 T）；struct 字段；
//   enum struct case 清单与参数洞载荷；String/List/Map/Array 字段的
//   容器键值/元素类型在 typeName 文本中保留（VM BIL 规范拼写）。
// native 面对拍（NativeE2E「字段反射对拍」）5-1b 已绿；JsonSerializer
// 消费属 5-2。

open class Base {
    pub var id: i32
    pub static var counter: i32 = 0
    pub init(_ -> id) { }
}

@Serializable()
class Derived : Base {
    pub var name: String?
    pub var score: double
    @Temporary(resume=(func{(): i32 -> 42} as core.Func\<i32>))
    pub var cache: i32 = 0
    pub init(baseId: i32, _ -> score) {
        super(baseId)
    }
}

class Holder\<T> {
    pub var item: T
    pub var tags: List\<T>
    pub init(_ -> item) {
        tags = new List\<T>()
    }
}

struct Point {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y) { }
}

enum struct Color {} [ Red -> 1, Green -> 2, Blue -> 3 ]

pub enum struct Shape {
    pub var area: double
    pub init(_ -> area) { }
} [ Circle(area = _), Square(area = _) ]

class Container {
    pub var text: String
    pub var scores: List\<i32>
    pub var lookup: Map\<String, i32>
    pub var raw: Array\<i32>
    pub init(_ -> text) {
        scores = new List\<i32>()
        lookup = new Map\<String, i32>()
        raw = arrayOf\<i32>(0)
    }
}

func findField(fields: Array\<FieldInfo>, name: String): FieldInfo? {
    var i: i32 = 0
    while (i < fields.length) {
        const current = fields[i] as FieldInfo
        if (current.name == name) { return current }
        i = i + 1
    }
    return null
}

pub func main(): i32 {
    // ---- 普通 class：继承字段并入、static/@Temporary 排除 ----
    const dFields = fieldsOf\<Derived>()
    if (dFields.length != 3) { return 1 }
    const idField = findField(dFields, "id")
    if (idField == null) { return 2 }
    if (idField.typeName != ".i32") { return 3 }
    if (idField.nullable) { return 4 }
    const nameField = findField(dFields, "name")
    if (nameField == null) { return 5 }
    if (not nameField.nullable) { return 6 }
    if (nameField.typeName != ".string") { return 7 }
    if (findField(dFields, "counter") != null) { return 8 }
    if (findField(dFields, "cache") != null) { return 9 }

    // ---- Type\<T\> 值形态与无参形态一致 ----
    const sample = new Derived(1, 2.0)
    if (fieldsOf(typeOf(sample)).length != 3) { return 10 }

    // ---- 泛型 class：闭合实参代入 ----
    const holder = new Holder\<i32>(7)
    const hFields = fieldsOf(typeOf(holder))
    if (hFields.length != 2) { return 11 }
    const itemField = findField(hFields, "item")
    if (itemField == null) { return 12 }
    if (itemField.typeName != ".i32") { return 13 }
    const tagsField = findField(hFields, "tags")
    if (tagsField == null) { return 14 }
    if (tagsField.typeName != "core.collections::List<.i32>") { return 15 }

    // ---- struct ----
    const pFields = fieldsOf\<Point>()
    if (pFields.length != 2) { return 16 }
    if (findField(pFields, "x") == null) { return 17 }
    if (findField(pFields, "y") == null) { return 18 }

    // ---- 容器字段：键值/元素类型保留 ----
    const box = new Container("x")
    const cFields = fieldsOf(typeOf(box))
    if (cFields.length != 4) { return 19 }
    const textField = findField(cFields, "text")
    if (textField == null) { return 20 }
    if (textField.typeName != ".string") { return 21 }
    const scoresField = findField(cFields, "scores")
    if (scoresField == null) { return 22 }
    if (scoresField.typeName != "core.collections::List<.i32>") { return 23 }
    const lookupField = findField(cFields, "lookup")
    if (lookupField == null) { return 24 }
    if (lookupField.typeName != "core.collections::Map<.string, .i32>") { return 25 }
    const rawField = findField(cFields, "raw")
    if (rawField == null) { return 26 }
    if (rawField.typeName != ".array<.i32>") { return 27 }

    // ---- enum struct：case 清单与载荷 ----
    const colors = casesOf\<Color>()
    if (colors.length != 3) { return 28 }
    const color0 = colors[0] as EnumCaseInfo
    const color1 = colors[1] as EnumCaseInfo
    const color2 = colors[2] as EnumCaseInfo
    if (((color0.name != "Red") or (color1.name != "Green"))
        or (color2.name != "Blue")) { return 29 }
    if (((color0.fields.length != 0) or (color1.fields.length != 0))
        or (color2.fields.length != 0)) { return 30 }
    const shapes = casesOf(typeOf(Shape.Circle(1.0)))
    if (shapes.length != 2) { return 31 }
    const shape0 = shapes[0] as EnumCaseInfo
    const shape1 = shapes[1] as EnumCaseInfo
    if ((shape0.name != "Circle") or (shape1.name != "Square")) { return 32 }
    if (shape0.fields.length != 1) { return 33 }
    const areaField = shape0.fields[0] as FieldInfo
    if (areaField.name != "area") { return 34 }
    if (areaField.typeName != ".f64") { return 35 }
    if (areaField.nullable) { return 36 }

    // ---- 标量/容器字段闭包为空数组（不抛未登记） ----
    if (fieldsOf\<i32>().length != 0) { return 37 }
    if (fieldsOf(typeOf("s")).length != 0) { return 38 }
    if (fieldsOf(typeOf(box.scores)).length != 0) { return 39 }

    // ---- typeNameOf：规范标识（含闭合泛型实参） ----
    if (typeNameOf(typeOf(sample)) != "Derived") { return 40 }
    if (typeNameOf(typeOf(holder)) != "Holder<.i32>") { return 41 }
    if (typeNameOf(typeOf(box.scores)) != "core.collections::List<.i32>") { return 42 }
    if (typeNameOf(typeOf(5)) != ".i32") { return 43 }

    // ---- typeNameOf\<T\>() 无参形态：与 Type\<T\> 值形态同一数据源 ----
    if (typeNameOf\<Derived>() != "Derived") { return 46 }
    if (typeNameOf\<i32>() != ".i32") { return 47 }
    if (typeNameOf\<Shape>() != "Shape") { return 48 }
    if (typeNameOf\<Holder\<i32>>() != "Holder<.i32>") { return 49 }

    // ---- isSerializable：能力查询（未知类型 false） ----
    if (not isSerializable(typeOf(sample))) { return 44 }
    if (not isSerializable(typeOf(5))) { return 45 }

    core.io.Console.println("reflection-fields-ok")
    return 0
}
