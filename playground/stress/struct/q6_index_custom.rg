// Q6 手工探针 2：自定义索引容器（运行时 getAtIndex 派发）+ 内建数组读写
import core.io.Console

pub class Bag {
    pub var item: i32
    pub init() { item = 5 }
    pub operator getAtIndex(index: i32): i32? { return item }
    pub operator setAtIndex(index: i32, element: i32) { item = element }
}

pub func main(): i32 {
    var b = new Bag()
    // 自定义容器读取：T?，if? 解包
    Console.println((b[0] if? -1).toString())
    // 写入仍收非空 T
    b[0] = 11
    Console.println((b[0] if? -1).toString())
    // 内建数组：越界 null、写入正常
    var arr = core.collections.arrayOfElements\<i32>(7, 8, 9)
    Console.println((arr[0] if? -1).toString())
    Console.println((arr[99] if? -1).toString())
    arr[1] = 42
    Console.println((arr[1] if? -1).toString())
    return 0
}
