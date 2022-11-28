open System.Net.Sockets;
open System.Net;
open System.Buffers.Binary;
open System.IO;
open FSharp.Data;

type Config = JsonProvider<"config.json">

type Req1 = uint8 * uint8 * uint8[]
type Req2 = uint8 * uint8 * uint8 *  uint8 * uint8[] * uint16 

type RespType =
    Resp1 of ver: uint8 * method: uint8 
    | Resp2 of ver: uint8 * rep : uint8 * rsv:uint8 * atyp : uint8 * addr: uint8[] * port: uint16

let readReq1 (stream: NetworkStream) =
    async {
        let! [|ver; nmethods|] = stream.AsyncRead 2
        let! methods = stream.AsyncRead  (int nmethods)
        return Req1(ver, nmethods, methods)
    }

let readReq2 (stream: NetworkStream) =
    async {
        let! [|ver; cmd; rsv; atyp|] = stream.AsyncRead 4
        let! addr = match int atyp with
                    | 1 -> stream.AsyncRead 4
                    | 4 -> stream.AsyncRead 16
        let! port_buf = stream.AsyncRead 2
        let port = BinaryPrimitives.ReadUInt16BigEndian(port_buf)
        return Req2(ver, cmd, rsv, atyp, addr, port)
    }

let writeResp (stream: NetworkStream) (resp: RespType) = 
    match resp with
        |Resp1(ver, method) -> stream.AsyncWrite [|ver; method|] 
        | Resp2(ver, rep, rsv, atyp, addr, port) -> let port_buf = [|uint8 0; uint8 0|]
                                                    BinaryPrimitives.WriteUInt16BigEndian(port_buf, port);
                                                    let buf = Array.concat [| [|ver; rep; rsv; atyp|]; addr; port_buf|]
                                                    stream.AsyncWrite buf

let listen (host: string, port: int32) = 
    let localAddr = IPAddress.Parse(host) 
    let server = new TcpListener(localAddr, port)
    server.Start();
    server

let handleProxyStream(aStream: NetworkStream) (bStream: NetworkStream) =
        [ aStream.CopyToAsync(bStream) |> Async.AwaitTask; bStream.CopyToAsync(aStream) |> Async.AwaitTask] |> Async.Parallel |> Async.Ignore

let handleStream (socks: Socket) =
    async {
            use socks = socks
            use stream = new NetworkStream(socks)
            let! (ver, nmethods, methods) = readReq1 stream
            do! writeResp stream (Resp1(ver, Array.head methods))
            let! (ver, cmd, rsv, atyp, addr, port) = readReq2 stream
            use client = new TcpClient()
            do! client.ConnectAsync(new IPAddress(addr), int port) |> Async.AwaitTask
            do! writeResp stream (Resp2(ver, uint8 0, rsv, atyp, addr, port))
            let serverStream = client.GetStream()
            do! handleProxyStream stream serverStream |> Async.Catch |> Async.Ignore
    }

let rec readerHeader(reader: StreamReader) = 
    async {
       let! line = reader.ReadLineAsync() |> Async.AwaitTask 
       if line = "" || line = "\r" then return [] else let! rest = readerHeader reader in  return line::rest
    }

let handleConnectStream (socks: Socket) =
    async {
       use socks = socks
       use stream = new NetworkStream(socks)
       use reader = new StreamReader(stream)
       let! lines = readerHeader reader
       let line = List.head lines
       let parts = line.Split(" ")
       let method = parts[0]
       let urlStr = parts[1]
       let ids =  urlStr.Split(":")
       let! hosts = Dns.GetHostAddressesAsync(ids[0], Sockets.AddressFamily.InterNetwork) |> Async.AwaitTask
       use client = new TcpClient()
       do! client.ConnectAsync(hosts[0], ids[1] |> int ) |> Async.AwaitTask
       let serverStream = client.GetStream()
       let resp = $"{parts[2]} 200 Connection Established\r\n\r\n"
       let resp_bytes = System.Text.Encoding.ASCII.GetBytes(resp)
       do! stream.AsyncWrite(resp_bytes)
       do! handleProxyStream stream serverStream |> Async.Catch |> Async.Ignore
    }

let rec startAccept (listener: TcpListener) (handle: Socket -> Async<unit>) =
    async {
        use! sock = listener.AcceptSocketAsync() |> Async.AwaitTask 
        handle sock |> Async.Start
        return! startAccept listener handle
    }

exception ErrorConfiguration

let config = Config.GetSample()
let genProxy (config: Config.Root2) = match config.Type with
                                        | "socks5" -> listen(config.Host, config.Port) |> (fun x -> startAccept x handleStream)
                                        | "http" -> listen(config.Host, config.Port) |> (fun x -> startAccept x handleConnectStream)
                                        | _ -> raise ErrorConfiguration

Array.map genProxy config.Root |> Async.Parallel |> Async.Ignore |> Async.RunSynchronously
