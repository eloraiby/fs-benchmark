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
open System
open Connection

type MessageHandler() =
    let map = ref (Map.empty<int, int>)

    interface IMessageHandler with
        member x.canBid (c, v) =
            match (!map).TryFind c with
            | Some v -> Answer.No
            | _ -> Answer.No
        member x.win (c, v) =
            match (!map).TryFind c with
            | Some v -> ()
            | _ -> ()

[<EntryPoint>]
let main argv =
    let conn = Connection.startConnectionHandler(Net.IPAddress.Parse("0.0.0.0") |> Some, 55555 |> Some, MessageHandler())
    printfn "Waiting for key to exit"
    System.Console.ReadKey()
    |> ignore
    0 // return an integer exit code
