// 约束证明保留合法的上下界、显式能力、宿主代入和递归转发。
// expect-exit: 0
open class Base { pub init() }
class Derived: Base { pub init() }
func upper\<T extends Base>(value: T): i32 { return 1 }
func forwardUpper\<T extends Derived>(value: T): i32 { return upper(value) }
func lower\<T supers Derived>(value: T): i32 { return 2 }
func forwardLower\<T supers Base>(value: T): i32 { return lower\<T>(value) }
func serialized\<T with core.serialization.Serializable>(value: T): i32 { return 3 }
func forwardSerialized\<T with core.serialization.Serializable>(value: T): i32 { return serialized(value) }
class Host\<T> {
    pub init()
    pub func accept\<U extends T>(value: U): i32 { return 4 }
    pub func forward\<U extends T>(value: U): i32 { return this.accept(value) }
    pub func acceptSuper\<U supers T>(value: U): i32 { return 5 }
    pub func forwardSuper\<U supers T>(value: U): i32 { return this.acceptSuper(value) }
    pub func pack\<U... extends T>(values: U...): i32 { return 8 }
    pub func forwardPack\<U extends T>(value: U): i32 { return this.pack(value) }
}
@WrapperTarget(.Entity)
wrapper Mark\<T> { pub init() }
@Mark
class Payload { pub init() }
class MarkHost\<T> {
    pub init()
    pub func accept\<U with Mark\<T>>(value: U): i32 { return 6 }
    pub func forward\<U with Mark\<T>>(value: U): i32 { return this.accept(value) }
}
pub func main(): i32 {
    if (forwardUpper(new Derived()) != 1) { return 1 }
    if (forwardLower(new Base()) != 2) { return 2 }
    if (forwardSerialized(17) != 3) { return 3 }
    var host = new Host\<Base>()
    if (host.accept(new Derived()) != 4) { return 4 }
    if (host.forward(new Derived()) != 4) { return 5 }
    if (host.forwardSuper(new Base()) != 5) { return 6 }
    var markHost = new MarkHost\<Payload>()
    if (markHost.forward(new Payload()) != 6) { return 7 }
    if (host.forwardPack(new Derived()) != 8) { return 8 }
    return 0
}
