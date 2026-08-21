// W2：泛型基类的静态字段不得使用类级 T（§9.2.3）
pub open class SGate\<T> {
    pub init()
    pub static var slot: T?
}
