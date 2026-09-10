// 泛型 wrapper 边界的宿主实参不能被擦除。
// expect-error: does not satisfy the 'With Mark<Other>'
@WrapperTarget(.Entity)
wrapper Mark\<T> { pub init() }
@Mark
class Payload { pub init() }
class Other { pub init() }
class Host\<T> {
    pub init()
    pub func accept\<U with Mark\<T>>(value: U) {}
}
pub func main(): i32 {
    var host = new Host\<Other>()
    host.accept(new Payload())
    return 0
}
