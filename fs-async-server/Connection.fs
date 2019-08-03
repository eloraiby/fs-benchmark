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
module Connection

open System
open FSharp.Control
open System.Buffers
open System.Net.Sockets
open System.Net
open System.Threading
open System.Text
open System.IO.Pipelines

open AsyncExt

let getCountersVals(parts: string[]) =
    parts.[1..]
    |> Array.map (fun part ->
        let counterIdValue = part.Split([|':'|])
        (counterIdValue.[0] |> Convert.ToInt32, counterIdValue.[1] |> Convert.ToInt32)
    )

type ConnectionMessage =
    | CanBid of (int * int)[]
    | Win of int * int
with
    // FIXME: This should return a Result, to send Err for bad command
   static member ofString (str: string) =
        let parts = str.Split([| ' '; '\n' |], StringSplitOptions.RemoveEmptyEntries)

        let command = parts.[0]
        match command with
        | "CB" ->
            let countersVals = getCountersVals(parts)
            CanBid countersVals
        | "W" -> Win(parts.[1] |> Convert.ToInt32, parts.[2] |> Convert.ToInt32)
        | _ -> failwith (sprintf "invalid command: \"%s\"" command)

    member this.shouldBroadcast() =
        match this with
        | CanBid _ -> false
        | Win _ -> true

    override x.ToString() =
        match x with
        | CanBid cs    ->
            let stringBuilder = System.Text.StringBuilder()
            stringBuilder.Append "CB" |> ignore<_>
            for (cid, value) in cs do
                stringBuilder.Append (sprintf " %O:%u" cid value) |> ignore<_>
            stringBuilder.Append "\n" |> ignore<_>
            stringBuilder.ToString()

        | Win (cid, value) -> sprintf "W %O %u\n" cid value

// answer
type Answer =
    | Yes
    | No
    | Ok
    | Err
with
    override x.ToString() =
        match x with
        | Yes -> "YES"
        | No -> "NO"
        | Ok -> "OK"
        | Err -> "ERR"

    static member ofString (str: string) =
        match str with
        | "YES" -> Yes
        | "NO" -> No
        | "OK" -> Ok
        | "ERR" -> Err
        | _ -> failwith (sprintf "invalid answer: \"%s\"" str)

    static member toString(answers: Answer[]) =
        let stringBuilder = Text.StringBuilder()
        answers
        |> Array.iteri (fun i answer ->
            if i <> 0 then
                stringBuilder.Append(' ') |> ignore
            stringBuilder.Append(answer.ToString()) |> ignore
        )
        stringBuilder.Append '\n' |> ignore
        stringBuilder.ToString()

type AnswerArray = Answers of Answer[]
with
    static member ofString (str: string) =
        str.Split([| ' '; '\n' |], StringSplitOptions.RemoveEmptyEntries )
        |> Array.map Answer.ofString
        |> Answers

type IMessageHandler =
    abstract canBid : int * int -> Answer
    abstract win    : int * int -> unit


let startConnectionHandler(ipAddress, port, processor: IMessageHandler) =
    let ipAddress = defaultArg ipAddress (IPAddress.Any)
    let port = defaultArg port 55555
    let endpoint = IPEndPoint(ipAddress, port)
    let cancellation = new CancellationTokenSource()
    let listener = new Socket(SocketType.Stream, ProtocolType.Tcp)
    listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1)
    listener.Bind(endpoint)
    listener.Listen(int SocketOptionName.MaxConnections)
    printfn "Started listening on port %d" port

    let asyncFillPipe (socket: Socket) (writer: PipeWriter) = async {
        let minimumBufferSize = 512
        let rec loop () = async {
            let memory = writer.GetMemory minimumBufferSize

            try
                let! bytesRead =
                    socket.ReceiveAsync(memory, SocketFlags.None)
                    |> Async.AwaitValueTask

                if bytesRead = 0 then
                    return ()

                writer.Advance bytesRead
            with e ->
                printfn "asyncFillPipe exception: %O" e
                return ()

            let! result =
                writer.FlushAsync ()
                |> Async.AwaitValueTask

            if result.IsCompleted then
                return ()
            else
                return! loop ()
        }
        do! loop ()
        writer.Complete()
    }

    let processLine id (socket: Socket) (buffer: ReadOnlySequence<byte>) = async {
        let mutable enumerator = buffer.GetEnumerator ()
        while enumerator.MoveNext() do
            let segment = enumerator.Current
            let line = Encoding.UTF8.GetString(segment.Span)
            let req = ConnectionMessage.ofString line

            match req with
            | Win (counterId, value) ->
                processor.win (counterId, value)
            | CanBid reqs ->
                let resp
                    = reqs
                    |> Array.map processor.canBid

                try
                    do!
                        resp
                        |> Answer.toString
                        |> Text.ASCIIEncoding.ASCII.GetBytes
                        |> socket.asyncSend

                with error ->
                    printfn "processRequest: An error occurred: %s" error.Message
    }

    let asyncReadPipe id (socket: Socket) (reader: PipeReader) = async {
        let rec loop () = async {
            let! result =
                reader.ReadAsync ()
                |> Async.AwaitValueTask

            let mutable buffer = result.Buffer
            let mutable first = true
            let mutable position : Nullable<SequencePosition> = Nullable()

            while first = true || position.HasValue do
                first <- false
                let position = buffer.PositionOf((byte)'\n')

                if position.HasValue then
                    let line = buffer.Slice(0, position.Value)
                    do! processLine id socket line
                    let next = buffer.GetPosition(1L, position.Value)
                    buffer <-  buffer.Slice next

            reader.AdvanceTo(buffer.Start, buffer.End)

            if result.IsCompleted then
                return ()
            else
                return! loop ()
        }
        do! loop ()
        reader.Complete()
    }

    let rec loop (id: int) = async {
        let! socket = listener.asyncAccept ()

        let pipe = new Pipe()
        let writing =
            asyncFillPipe socket pipe.Writer
            |> Async.StartAsTask
        let reading =
            asyncReadPipe id socket pipe.Reader
            |> Async.StartAsTask

        return! loop (id + 1)
    }

    Async.Start(loop(0), cancellationToken = cancellation.Token)
