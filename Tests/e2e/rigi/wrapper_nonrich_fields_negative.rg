// self 的豁免不能扩展到普通字段，也不能经泛型或嵌套 rich 值绕过。
// expect-error: cannot hold object field 'direct'
// expect-error: cannot embed rich value type field 'nested'
// expect-error: cannot hold object field 'item'
class Ref { }
rich struct RichValue { pub var value: Ref }
@WrapperTarget(.Entity)
wrapper BadDirect { var direct: Ref }
@WrapperTarget(.Entity)
wrapper BadNested { var nested: RichValue }
@WrapperTarget(.Entity)
wrapper GenericField\<T> { var item: T }
func bad(x: GenericField\<Ref>) { }
