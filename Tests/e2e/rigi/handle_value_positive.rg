// expect-output: 3
// expect-output: 9
// expect-output: 9
pub func main(): i32 {
    var value: i32 = 3
    unsafe seq using(const place = placeOf value) {
        const handle = place.expose()
        place.dispose()
        core.io.Console.println(handle.load().toString())
        const mutable = handle.asMutable()
        mutable.store(9)
        core.io.Console.println(value.toString())
        core.io.Console.println(handle.load().toString())
    }
    return 0
}
