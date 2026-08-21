// Q6 手工探针：索引读取一律返回 T?（§13.2）
// 预期输出：7 / -1 / 9 / null 语义（越界得 null，if? 回退）
import core.io.Console

pub func main(): i32 {
    var arr = core.collections.arrayOfElements\<i32>(7, 8, 9)
    // 界内读取：T?，if? 空值回退解包
    Console.println((arr[0] if? -1).toString())
    // 越界读取：得 null，if? 回退
    Console.println((arr[99] if? -1).toString())
    // 写入仍收非空 T，写后读回
    arr[1] = 42
    Console.println((arr[1] if? -1).toString())
    // null 判等 + smart cast 解包
    const last = arr[2]
    if (last != null) {
        Console.println(last.toString())
    }
    const oob = arr[5]
    if (oob == null) {
        Console.println("null")
    }
    return 0
}
