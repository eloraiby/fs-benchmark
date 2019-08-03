// Copyright (C) 2019 Wael El Oraiby
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
module AsyncExt

open System
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks

let dispose (d: IDisposable) = d.Dispose()


type System.DateTime
with
    member x.trimToHour() = System.DateTime(x.Year, x.Month, x.Day, x.Hour, 0, 0)

type System.Int32
with
    static member tryParse (s: string) =
        match System.Int32.TryParse(s) with
        | true, i -> Some i
        | _ -> None

type System.Net.IPAddress
with
    static member tryParse (s: string) =
        match System.Net.IPAddress.TryParse(s) with
        | true, i -> Some i
        | _ -> None

module Map =
    let removeKeys (keySet: Set<'K>) (m: Map<'K, 'V>) =
        m
        |> Map.filter(fun k v ->
            keySet.Contains k
            |> not
        )

    let toKeySet(m: Map<'K, 'V>) =
        m
        |> Map.toArray
        |> Array.map fst
        |> Set.ofArray

    let choose f (m: Map<'a, 'b>) : Map<'a, 'c> =
        m
        |> Seq.choose (fun (KeyValue(k, v1)) ->
            f k v1
            |> Option.map (fun v2 -> (k, v2))
        )
        |> Map.ofSeq

    let singleton key value =
        Map.add key value Map.empty

    let values m = Seq.map (fun (KeyValue(_, value)) -> value) m

    let updateWith key fn init map =
        let value =
            map
            |> Map.tryFind key
            |> Option.map fn
            |> Option.defaultValue init
        Map.add key value map

module Async =
    let map f op = async {
        let! x    = op
        let value = f x
        return value
    }

    let AwaitValueTask (x: System.Threading.Tasks.ValueTask<'t>) =
        Async.AwaitTask(x.AsTask())

    let AwaitUnitTask (x: System.Threading.Tasks.Task) =
        x.ContinueWith(fun _ -> ()) |> Async.AwaitTask

module Pair =
    let mapfst f (a, b) = (f a, b)
    let mapsnd f (a, b) = (a, f b)
    let key (KeyValue(key, _)) = key
    let value (KeyValue(_, value)) = value

module Seq =
    let tryMinBy f xs =
        if Seq.isEmpty xs then
            None
        else
            xs
            |> Seq.minBy f
            |> Some

    let tryMaxBy f xs =
        if Seq.isEmpty xs then
            None
        else
            xs
            |> Seq.maxBy f
            |> Some

let (|>!) a f = f a; a

type System.Threading.Tasks.ValueTask<'t>
with
    static member asTask (x: System.Threading.Tasks.ValueTask<'t>) =
        x.AsTask()

let str2bytes (str: string) = ASCIIEncoding.ASCII.GetBytes str

let bytes2str (bytes: byte[]) = ASCIIEncoding.ASCII.GetString bytes

type Map<'K, 'V when 'K : comparison>
with
    static member diff (mergeFn: 'V -> 'V -> 'V) (a: Map<'K, 'V>) (b: Map<'K, 'V>) =
        let sa
            = a
            |> Map.toArray

        sa
        |> Array.fold(fun (state: Map<'K, 'V>) (k, v) ->
            match b.TryFind k with
            | Some vb -> state.Add(k, mergeFn vb v)
            | None    -> state.Add(k, v)) a


    static member union (mergeFn: 'V -> 'V -> 'V) (a: Map<'K, 'V>) (b: Map<'K, 'V>) =
        let sb = b |> Map.toArray

        sb
        |> Array.fold(fun (state: Map<'K, 'V>) (k, vb) ->
            match state.TryFind k with
            | Some va -> state.Add(k, mergeFn va vb)
            | None    -> state.Add(k, vb)) a

type Microsoft.FSharp.Control.Async with
    static member awaitTask (t : Task<'T>, timeout : int) =
        async {
            use cts = new CancellationTokenSource()
            use timer = Task.Delay (timeout, cts.Token)
            let! completed = Async.AwaitTask <| Task.WhenAny(t, timer)
            if completed = (t :> Task) then
                cts.Cancel ()
                let! result = Async.AwaitTask t
                return Some result
            else return None
        }

    static member await (timeout : int) (t : Async<'T>)  =
        Async.awaitTask (t |> Async.StartAsTask, timeout)

let [<Literal>] MAX_BUFFER_LEN  = 8192

//
// Handy socket extensions
//
type Socket with
    member socket.asyncAccept() = Async.FromBeginEnd(socket.BeginAccept, socket.EndAccept)
    member socket.asyncReceive() = async {
        let buffer : byte[] = Array.zeroCreate MAX_BUFFER_LEN
        let beginReceive(b,o,c,cb,s) = socket.BeginReceive(b,o,c,SocketFlags.None,cb,s)
        let! len = Async.FromBeginEnd(buffer, 0, MAX_BUFFER_LEN, beginReceive, socket.EndReceive)
        return buffer.[0..len - 1]
    }

    member socket.asyncSend(buffer : byte[]) = async {
        let beginSend(b,o,c,cb,s) = socket.BeginSend(b,o,c,SocketFlags.None,cb,s)
        let rec loop(pos: int) = async {
            let! len = Async.FromBeginEnd(buffer, pos, buffer.Length - pos, beginSend, socket.EndSend)
            if pos + len = buffer.Length
            then ()
            else return! loop(pos + len)
        }
        return! loop 0
    }

    member socket.asyncConnect(ep) =
        let bc ep (cb, s) = socket.BeginConnect(ep, cb, s)
        Async.FromBeginEnd(bc ep, socket.EndConnect)

    member socket.asyncReceiveByte(timeout: int) = async {
        let buffer : byte[] = Array.zeroCreate 1
        let beginReceive(b,o,c,cb,s) = socket.BeginReceive(b,o,c,SocketFlags.None,cb,s)
        let! res = Async.FromBeginEnd(buffer, 0, 1, beginReceive, socket.EndReceive)
                |> Async.await timeout
        match res with
        | Some len -> return buffer.[0]
        | None -> return failwith "no more data"
    }

    member x.asyncReceiveLine() = async {
        let mutable ch = 0uy
        let! ch_ = x.asyncReceiveByte 1000
        let data = System.Collections.Generic.List<byte>()
        ch <- ch_
        data.Add ch
        while ch <> ('\n' |> byte) do
            let! ch_ = x.asyncReceiveByte 1000
            ch <- ch_
            data.Add ch

        return data |> Seq.toArray
    }

type NetworkStream
with
    member x.asyncWrite (buffer: byte[]) =
        x.WriteAsync(buffer, 0, buffer.Length)
        |> Async.AwaitTask

    member x.asyncRead timeout = async {
        let buffer = Array.zeroCreate MAX_BUFFER_LEN
        let! count
            = x.ReadAsync(buffer, 0, buffer.Length)
            |> Async.AwaitTask

        return buffer.[0..count - 1]
    }

type private Queue<'T> = Collections.Generic.Queue<'T>

type BufferedTcpSocket
    = private { lines   : Queue<string>
                temp    : ResizeArray<byte>
                stream  : NetworkStream }
with
    static member createAsyncConnect(endPoint: Net.IPEndPoint) = async {
        let client = new Socket(SocketType.Stream, ProtocolType.Tcp)
        client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1)
        do! client.asyncConnect endPoint
        return { stream = new NetworkStream(client, true); lines = Queue(); temp = ResizeArray() }
    }

    member private x.asyncReadLines() = async {
        let rec readPacket() = async {
            let! bytes = x.stream.asyncRead()
            let mutable foundLine = false
            for b in bytes do
                if b = ('\n' |> byte)
                then
                    x.temp.ToArray()
                    |> bytes2str
                    |> x.lines.Enqueue
                    x.temp.Clear()
                    foundLine <- true
                else
                    b
                    |> x.temp.Add
            if foundLine
            then ()
            else return! readPacket()
        }


        return! readPacket()
    }

    member x.asyncReadLine() = async {

        match x.lines.Count with
        | 0 ->
            do! x.asyncReadLines()
            return! x.asyncReadLine()
        | k -> return x.lines.Dequeue()
    }

    member x.asyncWriteLine str =
        str
        |> str2bytes
        |> x.stream.asyncWrite

    interface IDisposable with
        member x.Dispose() = dispose x.stream