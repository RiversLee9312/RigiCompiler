// MW7b 负例：Span<class> 违反 T extends ValueType
// expect-error: Type argument 'Widget' does not satisfy the 'Extends ValueType' constraint of 'T'
class Widget {
    pub var n: i32
    pub init(_ -> n)
}
pub func take(s: Span\<Widget>): i32 {
    return 0
}
pub func main(): i32 {
    return 0
}
