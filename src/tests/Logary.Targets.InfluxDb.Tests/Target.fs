﻿module Logary.Targets.InfluxDb.Tests.TargetTests

open NodaTime
open System
open System.Text
open System.Threading
open Logary
open Logary.Configuration
open Logary.Internals
open Logary.Targets.InfluxDb
open Expecto
open Suave
open Suave.Operators
open Hopac
open Hopac.Extensions
open Hopac.Infixes
open TestHelpers

let emptyRuntime = RuntimeInfo.create "tests"

let flush = Target.flush >> Job.Ignore >> run

let start () =
  let targConf =
    InfluxDbConf.create(Uri "http://127.0.0.1:9011/write", "tests")

  Target.init emptyRuntime (create targConf "influxdb")
  |> run
  |> fun inst -> inst.server (fun _ -> Job.result ()) None |> start; inst

let finaliseTarget = Target.shutdown >> fun a ->
  a ^-> TimeoutResult.Success <|> timeOutMillis 1000 ^->. TimedOut
  |> run
  |> function
  | TimedOut -> Tests.failtest "finalising target timeout"
  | TimeoutResult.Success _ -> ()

type State(cts : CancellationTokenSource) =
  let request = Ch ()

  member x.req = request

  interface IDisposable with
    member x.Dispose () =
      cts.Cancel()
      cts.Dispose()

[<Tests>]
let writesOverHttp =

  let withServer () =
    let cts = new CancellationTokenSource()
    let state = new State(cts)
    let cfg =
      { defaultConfig with bindings = [ HttpBinding.createSimple HTTP "127.0.0.1" 9011 ]
                           cancellationToken = cts.Token }
    let listening, srv =
      startWebServerAsync cfg (request (fun r ctx -> async {
        do! Job.toAsync (Ch.give state.req r)
        return! Successful.NO_CONTENT ctx 
      }))
    Async.Start(srv, cts.Token)
    listening |> Async.Ignore |> Async.RunSynchronously
    state

  testList "writes over HTTP" [
    testCase "write to Suave" <| fun _ ->
      let msg =
        Message.gaugeWithUnit (PointName.parse "Processor.% User Time") Percent (Int64 1L)
        |> Message.setField "inst1" (Float 0.3463)
        |> Message.setField "inst2" (Float 0.223)
        |> Message.setContext "service" "svc-2"
        |> Message.tag KnownLiterals.SuppressPointValue
        |> Message.tag "my-tag"
        |> Message.tag "ext"
      //printfn "tags: %A" msg.context.["tags"]

      use state = withServer ()

      let target = start ()
      try
        msg
        |> Target.log target
        |> run  
        |> ignore

        let req =
          Ch.take state.req
          |> run

        let expected = Serialisation.serialiseMessage msg
        stringEqual (req.rawForm |> Encoding.UTF8.GetString)
                    expected
                    "should eq"
        //printfn "expected: %s" expected

        Expect.equal (req.queryParam "db") (Choice1Of2 "tests") "should write to tests db"

      finally
        finaliseTarget target
        
         
    testCase "write to Suave in batch" <| fun _ ->
      let msg = Message.gauge (PointName.parse "Number 1") (Float 0.3463)
      let msg2 = Message.gauge (PointName.parse "Number 2") (Float 0.3463)
      let msg3 = Message.gauge (PointName.parse "Number 3") (Float 0.3463)

      use state = withServer ()
      let target = start ()
          
      try
        
        let p1 = Target.log target msg 
                 |> run       

        let p2 = Target.log target msg2
                 |> run

        let p3 = Target.log target msg3
                 |> run

        let req = Ch.take state.req 
                  |> run

        let req2 = Ch.take state.req
                   |> run
        

        stringEqual (req2.rawForm |> Encoding.UTF8.GetString)
                     (Serialisation.serialiseMessage msg2 + "\n" + Serialisation.serialiseMessage msg3 )
                     "should eq"

        Expect.equal (req.queryParam "db") (Choice1Of2 "tests") "should write to tests db"

      finally
        finaliseTarget target

    testCase "make sure target acks" <| fun _ ->
      let msg = Message.gauge (PointName.parse "Number 1") (Float 0.3463)

      use state = withServer ()
      let target = start ()
        
      try
        let ackPromise = Target.log target msg 
                         |> run

        let req2 = Ch.take state.req
                   |> run
        
        Alt.choose ([
                    ackPromise :> Alt<_>
                    timeOut (TimeSpan.FromMilliseconds 100.0)
                   ]) |> run

        let messageIsAcked = Promise.Now.isFulfilled ackPromise

        Expect.isTrue messageIsAcked "message should be acked"

      finally
        finaliseTarget target
  ]
