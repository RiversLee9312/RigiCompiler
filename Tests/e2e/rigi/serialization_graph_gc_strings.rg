import core.collections.*
// 回收闭环中的动态String字段、String数组与rich值字段，不能重入mutator fence。
// expect-output: strings-ok
rich struct RichText {
    pub var text: String
    pub init(value: String) { text = value }
}
class StringCycle {
    pub var next: StringCycle?
    pub var text: String
    pub var texts: Array\<String>
    pub var richText: RichText
    pub init(n: i32) {
        text = n.toString()
        texts = arrayOfElements\<String>((n + 1).toString())
        richText = new RichText((n + 2).toString())
    }
}
pub func main(): i32 {
    const node = new StringCycle(41)
    node.next = node
    core.io.Console.println("strings-ok")
    return 0
}
