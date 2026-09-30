import core.serialization.*
// expect-output: enum-snapshot-ok
// expect-exit: 0
@Serializable
pub enum struct Marker {} [Cold -> 3, Hot -> 91]
@Serializable
pub class Item {
    pub var value: i32
    pub var owner: Envelope?
    pub init(_ -> value)
}
@Serializable
pub rich enum struct Event {
    pub var payload: Item
    pub const code: i32
    pub init(_ -> payload, _ -> code)
}[First(payload = _, code = _) -> 17, Second(payload = _, code = _) -> 900]
@Serializable
pub class Envelope {
    pub var event: Event
    pub init(_ -> event)
}
pub func main(): i32 {
    if (not (deepCopy(Marker.Hot) is .Hot)) { return 10 }
    if (not (deepCopy(Marker.Cold, true) is .Cold)) { return 11 }
    const item = new Item(7)
    const source = new Envelope(Event.Second(item, 31))
    const wire = source:Serializable.toParcel()
    item.value = 99
    const copy = fromParcel\<Envelope>(wire)
    if (not (copy.event is .Second)) { return 1 }
    if ((copy.event.payload.value != 7) or (copy.event.code != 31)) { return 2 }
    copy.event.payload.value = 21
    const again = fromParcel\<Envelope>(wire)
    if ((again.event.payload.value != 7) or (item.value != 99)) { return 3 }
    const root = deepCopy(source.event)
    if ((not (root is .Second)) or (root.payload.value != 99)) { return 4 }
    const malformed = source.event:Serializable.toParcel()
    malformed.setMetaElement\<String>("..case", "Missing")
    var rejected = false
    try { const bad = fromParcel\<Event>(malformed) }
    catch (e: IllegalStateException) { rejected = true }
    if (not rejected) { return 5 }
    item.owner = source
    rejected = false
    try { const bad = deepCopy(source) }
    catch (e: IllegalStateException) { rejected = true }
    if (not rejected) { return 6 }
    const graph = deepCopy(source, true)
    const back = graph.event.payload.owner as Envelope
    seq using(const left = placeOf graph) using(const right = placeOf back) {
        if (left != right) { return 7 }
    }
    if ((not (graph.event is .Second)) or (graph.event.code != 31)) { return 8 }
    graph.event.payload.value = 123
    if (item.value != 99) { return 9 }
    core.io.Console.println("enum-snapshot-ok")
    return 0
}
