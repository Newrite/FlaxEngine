/// Warm F# compiler host and its client.
///
/// One executable, two modes:
///   FlaxFSharpCompiler --serve <pipe>     run as the host (started automatically, not by hand)
///   FlaxFSharpCompiler <response-file>    client: compile through the host, or fall back to fsc
///
/// The client is what the build invokes. It never fails the build because of the host: any
/// problem connecting, starting, or talking to it falls through to a plain fsc process, which is
/// exactly what the build did before this existed.
///
/// Output channels matter: Flax.Build logs every line a task writes to stderr as an error, and
/// stdout as information. So only errors go to stderr; the fallback notice and the verbose trace
/// (FLAX_FSHARP_HOST_VERBOSE=1) go to stdout.
module FlaxFSharpCompiler.Program

open System
open System.Diagnostics
open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

/// Bumped when the wire protocol changes, so a stale host from an older engine build is never
/// talked to. It is part of the pipe name rather than a handshake, so a mismatch simply starts a
/// separate host instead of failing.
let [<Literal>] ProtocolVersion = "1"

/// The host exits on its own after this long without work, so a forgotten process does not sit on
/// ~400 MB indefinitely.
let IdleTimeout = TimeSpan.FromMinutes 30.0

/// The host log is shared by every host a user starts and gains a couple of lines per compile; a
/// host starting with a log larger than this starts a new one, keeping the previous as .old.log.
let [<Literal>] MaxLogSize = 1048576L

let [<Literal>] ExitOk = 0
let [<Literal>] ExitCompileFailed = 1
let [<Literal>] ExitProtocolError = 90

// ---------------------------------------------------------------------------------------------
// Diagnostics formatting
// ---------------------------------------------------------------------------------------------

/// Renders a diagnostic the way the editor's Output Log expects it.
///
/// OutputLogWindow matches one regex against compiler output and needs an absolute path plus the
/// four-number (line,col,endLine,endCol) span. That is what fsc emits under --vserrors; FCS hands
/// back a structured record instead, so the same shape is rebuilt here. The severity word is
/// preceded by a subcategory ("typecheck error FS0001") exactly as fsc writes it, which the
/// engine-side pattern was widened to accept.
let formatDiagnostic (d: FSharpDiagnostic) =
    let severity =
        match d.Severity with
        | FSharpDiagnosticSeverity.Error -> "error"
        | FSharpDiagnosticSeverity.Warning -> "warning"
        | FSharpDiagnosticSeverity.Info -> "info"
        | FSharpDiagnosticSeverity.Hidden -> "info"
    let subcategory = if String.IsNullOrEmpty d.Subcategory then "" else d.Subcategory + " "
    // A diagnostic with no file (a command-line problem, say) still has to be printed, just
    // without the clickable prefix.
    if String.IsNullOrEmpty d.Range.FileName || d.Range.FileName = "unknown" then
        sprintf "%s%s FS%04d: %s" subcategory severity d.ErrorNumber d.Message
    else
        let path =
            try Path.GetFullPath d.Range.FileName
            with _ -> d.Range.FileName
        // Newlines would break the one-diagnostic-per-line contract the log parser relies on;
        // fsc solves this with --flaterrors and so do we.
        let message = d.Message.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ")
        sprintf "%s(%d,%d,%d,%d): %s%s FS%04d: %s"
            path d.Range.StartLine (d.Range.StartColumn + 1) d.Range.EndLine (d.Range.EndColumn + 1)
            subcategory severity d.ErrorNumber message

// ---------------------------------------------------------------------------------------------
// Compilation
// ---------------------------------------------------------------------------------------------

let readResponseFile (path: string) =
    File.ReadAllLines path
    |> Array.map (fun line -> line.Trim())
    |> Array.filter (fun line -> line <> "" && not (line.StartsWith "#"))

/// Compiles one response file and returns (exitCode, formatted diagnostics).
///
/// FCS carries process-global state and historically could not run two compilations at once, so
/// callers must serialise. The host does that with a single worker; nothing here is thread safe.
let compile (checker: FSharpChecker) (responseFile: string) =
    try
        let args = Array.append [| "fsc.exe" |] (readResponseFile responseFile)
        // FCS reports failure as an optional exception plus the diagnostics, not as an exit code.
        // Anything of Error severity has to fail the build too, or a compile that produced no
        // assembly would be reported as success and the stale DLL would silently be reused.
        let diagnostics, failure =
            checker.Compile(args, userOpName = "FlaxFSharpCompiler")
            |> Async.RunSynchronously
        let lines = ResizeArray(diagnostics |> Array.map formatDiagnostic)
        let hasErrors =
            diagnostics |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
        match failure with
        | Some ex ->
            lines.Add(sprintf "error FS0000: F# compilation failed: %s: %s"
                          (ex.GetType().Name) (ex.Message.Replace("\n", " ").Replace("\r", " ")))
            (ExitCompileFailed, lines.ToArray())
        | None ->
            ((if hasErrors then ExitCompileFailed else ExitOk), lines.ToArray())
    with ex ->
        (ExitProtocolError, [| sprintf "error FS0000: F# compiler host failed: %s: %s" (ex.GetType().Name) ex.Message |])


// ---------------------------------------------------------------------------------------------
// Transport
//
// Requests and replies travel through files in a work directory, not through a named pipe.
//
// The pipe version is preserved in git history and it did not work: the host accepted the
// connection and then blocked in ReadLine forever while the client blocked waiting for the reply,
// with no error on either side. An independent PowerShell client hung identically, so the fault
// was in the server half rather than in the client code. Chasing Windows named-pipe semantics was
// not worth it for a mechanism whose entire purpose is to save about a second: a request file, a
// reply file and a poll are perhaps 10-20 ms of overhead and have no subtleties at all.
//
// Protocol, one exchange per request id:
//   <work>/<id>.req      client writes: the fsc response file path (one line)
//   <work>/<id>.tmp      host writes the reply here, then renames - so the client never observes
//                        a half-written file
//   <work>/<id>.res      last line is "EXIT <code>"; everything before it is diagnostics
// ---------------------------------------------------------------------------------------------

/// The user part of the work directory name.
let userStamp () =
    // Must be reproducible across processes so a later client finds the running host's directory.
    // String.GetHashCode cannot be used: .NET Core randomises it per process, which had the
    // client looking in one place while the host it had just started served another.
    use sha = System.Security.Cryptography.SHA256.Create()
    let bytes = sha.ComputeHash(Text.Encoding.UTF8.GetBytes Environment.UserName)
    Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant()

let workDirFor (hostPath: string) =
    let stamp =
        try File.GetLastWriteTimeUtc(hostPath).Ticks.ToString("x")
        with _ -> "0"
    Path.Combine(Path.GetTempPath(), sprintf "FlaxFSharpCompiler.%s.%s.%s" ProtocolVersion stamp (userStamp ()))

/// Touched by the host on every poll. Its freshness is how a client knows a host is alive without
/// process handles or platform-specific lookups.
let heartbeatPath (workDir: string) = Path.Combine(workDir, "host.alive")

let hostIsAlive (workDir: string) =
    try
        let hb = heartbeatPath workDir
        File.Exists hb && (DateTime.UtcNow - File.GetLastWriteTimeUtc hb) < TimeSpan.FromSeconds 10.0
    with _ -> false

// ---------------------------------------------------------------------------------------------
// Host
// ---------------------------------------------------------------------------------------------

/// Removes the work directories of this user that no live host uses.
///
/// The work directory name carries the executable's timestamp, so every rebuild of the tool (and
/// every protocol version) gets a new one and nothing else removes the old ones. A live host - of
/// another engine checkout, say - keeps its heartbeat fresh and is left alone.
let removeStaleWorkDirs (workDir: string) (log: string -> unit) =
    try
        let own = Path.GetFullPath workDir
        for dir in Directory.GetDirectories(Path.GetTempPath(), sprintf "FlaxFSharpCompiler.*.%s" (userStamp ())) do
            if not (String.Equals(Path.GetFullPath dir, own, StringComparison.OrdinalIgnoreCase)) && not (hostIsAlive dir) then
                try
                    Directory.Delete(dir, true)
                    log (sprintf "removed stale work dir %s" dir)
                with _ -> ()
    with _ -> ()

let runServer (workDir: string) =
    Directory.CreateDirectory workDir |> ignore

    // One host per work directory. Clients that arrive together while no host is alive each
    // start one; without this lock several hosts would serve the same queue, and --shutdown,
    // which stops one, would leave the rest holding the executable locked. The lock is held for
    // the host's lifetime and released by the OS however the process ends.
    let hostLock =
        try Some(new FileStream(Path.Combine(workDir, "host.lock"), FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.None))
        with _ -> None
    if hostLock.IsNone then 0 else

    // A host started through ShellExecute has no usable console; print to a log file so host
    // failures stay inspectable. Only the lock owner gets here, so there is one writer.
    let logPath = Path.Combine(Path.GetTempPath(), "FlaxFSharpCompiler.host.log")
    let log (msg: string) =
        try Console.Out.WriteLine(sprintf "[%O] %s" DateTime.Now msg) with _ -> ()
    try
        // Start over once the log has grown large, keeping the previous one
        let info = FileInfo logPath
        if info.Exists && info.Length > MaxLogSize then
            File.Move(logPath, Path.ChangeExtension(logPath, ".old.log"), true)
    with _ -> ()
    try
        let logStream = new StreamWriter(logPath, append = true)
        logStream.AutoFlush <- true
        Console.SetOut logStream
        Console.SetError logStream
    with _ -> ()

    log (sprintf "host starting, work=%s" workDir)
    removeStaleWorkDirs workDir log

    let checker = FSharpChecker.Create()
    log "checker ready"

    let mutable lastWork = DateTime.UtcNow
    let mutable running = true

    while running do
        try
            File.WriteAllText(heartbeatPath workDir, string DateTime.UtcNow.Ticks)
        with _ -> ()

        let requests =
            try Directory.GetFiles(workDir, "*.req") |> Array.sortBy File.GetLastWriteTimeUtc
            with _ -> [||]

        if File.Exists(Path.Combine(workDir, "shutdown")) then
            // A warm host holds its own executable locked, so rebuilding the tool needs a way to
            // stop it that is not Stop-Process.
            log "shutdown requested"
            (try File.Delete(Path.Combine(workDir, "shutdown")) with _ -> ())
            (try File.Delete(heartbeatPath workDir) with _ -> ())
            running <- false
        elif requests.Length = 0 then
            if DateTime.UtcNow - lastWork > IdleTimeout then
                log "idle timeout, exiting"
                running <- false
            else
                // 15 ms is well under the noise floor of a compile and keeps the host's idle CPU
                // cost invisible.
                Thread.Sleep 15
        else
            for request in requests do
                try
                    let id = Path.GetFileNameWithoutExtension request
                    // Read then delete: the .req file disappearing is what stops the same request
                    // being served twice if a reply write fails.
                    let responseFile = (File.ReadAllText request).Trim()
                    try File.Delete request with _ -> ()

                    log (sprintf "compiling %s" responseFile)
                    let exitCode, lines = compile checker responseFile
                    log (sprintf "done exit=%d diagnostics=%d" exitCode lines.Length)

                    let body =
                        String.Join(Environment.NewLine,
                                    Array.append lines [| sprintf "EXIT %d" exitCode |])
                    let tmp = Path.Combine(workDir, id + ".tmp")
                    let res = Path.Combine(workDir, id + ".res")
                    File.WriteAllText(tmp, body)
                    // Rename is atomic enough here: the client only ever opens the .res name, so
                    // it cannot read a partially written reply.
                    File.Move(tmp, res, true)
                with ex ->
                    log (sprintf "request failed: %s: %s" (ex.GetType().Name) ex.Message)
            lastWork <- DateTime.UtcNow
    // The lock is otherwise unreferenced after startup; a finalized FileStream would release it.
    GC.KeepAlive hostLock
    0

// ---------------------------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------------------------

/// Starts a detached host.
///
/// The host must inherit NO handles from the client. With UseShellExecute=false, .NET calls
/// CreateProcess with bInheritHandles=TRUE, so the host inherits every inheritable handle the
/// client holds - including the stdout/stderr pipes the build tool gave the client. The host
/// outlives the client by up to IdleTimeout, and a caller that reads the client's output to EOF
/// (Flax.Build's LocalExecutor does) then hangs until the host exits. Redirecting the host's own
/// streams does not help: that only adds new pipes, it does not stop the inheritance.
/// ShellExecute never inherits handles. Note it ignores ArgumentList, so the arguments go
/// through the Arguments string - using ArgumentList here starts a host with no arguments,
/// which prints its usage and exits.
let startHost (hostPath: string) (workDir: string) =
    try
        let self = Environment.ProcessPath
        if isNull self then false
        else
            let quote (s: string) = "\"" + s.TrimEnd('\\') + "\""
            let psi = ProcessStartInfo()
            psi.FileName <- self
            let prefix =
                if Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) then
                    quote (Reflection.Assembly.GetEntryAssembly().Location) + " "
                else ""
            psi.Arguments <- prefix + "--serve " + quote workDir
            psi.UseShellExecute <- true
            psi.WindowStyle <- ProcessWindowStyle.Hidden
            psi.WorkingDirectory <- Path.GetDirectoryName hostPath
            Process.Start psi |> ignore
            true
    with _ -> false

/// Sends one request and waits for the reply. Returns None if no reply arrived in time.
let tryCompileViaHost (workDir: string) (responseFile: string) (timeout: TimeSpan) =
    try
        Directory.CreateDirectory workDir |> ignore
        let id = Guid.NewGuid().ToString "N"
        let req = Path.Combine(workDir, id + ".req")
        let res = Path.Combine(workDir, id + ".res")
        File.WriteAllText(req, responseFile)

        let deadline = DateTime.UtcNow + timeout
        let mutable result = None
        while result.IsNone && DateTime.UtcNow < deadline do
            if File.Exists res then
                let lines = File.ReadAllLines res
                try File.Delete res with _ -> ()
                let exitLine = lines |> Array.tryFindBack (fun l -> l.StartsWith "EXIT ")
                match exitLine with
                | Some line ->
                    let code = Int32.Parse(line.Substring 5)
                    let diagnostics = lines |> Array.filter (fun l -> not (l.StartsWith "EXIT "))
                    result <- Some(code, diagnostics)
                | None -> result <- Some(ExitProtocolError, [| "error FS0000: malformed reply from F# compiler host" |])
            else
                Thread.Sleep 10

        if result.IsNone then
            // Leaving a stale .req behind would make the host compile something nobody is waiting
            // for on its next poll.
            try File.Delete req with _ -> ()
        result
    with ex ->
        if Environment.GetEnvironmentVariable "FLAX_FSHARP_HOST_VERBOSE" = "1" then
            printfn "FlaxFSharpCompiler: host request failed: %s: %s" (ex.GetType().Name) ex.Message
        None

/// Last resort: run the build exactly as it ran before this tool existed.
///
/// The response file is a plain fsc response file, so fsc consumes it unchanged. Anything wrong
/// with the host degrades to the previous behaviour and a slower build, never to a failed one.
/// The fsc path is supplied by the caller rather than guessed, because Flax.Build already
/// resolved the SDK and guessing it twice is how the two drift apart.
let fallbackToFsc (fscPath: string) (responseFile: string) =
    try
        if String.IsNullOrEmpty fscPath || not (File.Exists fscPath) then
            eprintfn "error FS0000: F# compiler host unavailable and no usable fsc path was supplied"
            ExitProtocolError
        else
            // A slower build, not a failed one: information, so stdout (see the module comment)
            printfn "FlaxFSharpCompiler: host unavailable, falling back to fsc"
            let dotnet =
                let d = Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
                if String.IsNullOrEmpty d then "dotnet" else d
            let psi = ProcessStartInfo(dotnet)
            psi.ArgumentList.Add "exec"
            psi.ArgumentList.Add fscPath
            psi.ArgumentList.Add("@" + responseFile)
            psi.UseShellExecute <- false
            use p = Process.Start psi
            p.WaitForExit()
            p.ExitCode
    with ex ->
        eprintfn "error FS0000: fallback to fsc failed: %s: %s" (ex.GetType().Name) ex.Message
        ExitProtocolError

let runClient (responseFile: string) (fscPath: string) =
    // The host runs in another directory, so a relative path would resolve against the wrong one.
    let responseFile = Path.GetFullPath responseFile
    if not (File.Exists responseFile) then
        eprintfn "error FS0000: response file not found: %s" responseFile
        ExitProtocolError
    else

    let verbose = Environment.GetEnvironmentVariable "FLAX_FSHARP_HOST_VERBOSE" = "1"
    let trace (msg: string) = if verbose then printfn "FlaxFSharpCompiler: %s" msg

    let hostPath = Environment.ProcessPath
    let workDir = workDirFor hostPath
    trace (sprintf "work=%s alive=%b" workDir (hostIsAlive workDir))

    let result =
        // FLAX_FSHARP_HOST_DISABLE=1 forces the fsc fallback: an escape hatch if the host ever
        // misbehaves, and the only way to exercise the fallback path deterministically in tests.
        if Environment.GetEnvironmentVariable "FLAX_FSHARP_HOST_DISABLE" = "1" then
            trace "host disabled by FLAX_FSHARP_HOST_DISABLE"
            None
        elif hostIsAlive workDir then
            // A live host answers well inside this; the allowance is for a large project, not for
            // waiting on startup.
            tryCompileViaHost workDir responseFile (TimeSpan.FromMinutes 5.0)
        else
            let started = startHost hostPath workDir
            trace (sprintf "startHost returned %b" started)
            if started then
                // Wait for the heartbeat rather than for the reply, so a host that never comes up
                // is detected in seconds instead of after a full compile timeout.
                let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 30.0
                while not (hostIsAlive workDir) && DateTime.UtcNow < deadline do
                    Thread.Sleep 100
                if hostIsAlive workDir then
                    trace "host is up"
                    tryCompileViaHost workDir responseFile (TimeSpan.FromMinutes 5.0)
                else
                    trace "host never signalled"
                    None
            else None

    match result with
    | Some (exitCode, lines) when exitCode = ExitProtocolError ->
        // The host itself failed (not the user's code): that is a host problem like any other,
        // and host problems degrade to plain fsc rather than fail the build.
        for line in lines do trace line
        fallbackToFsc fscPath responseFile
    | Some (exitCode, lines) ->
        for line in lines do Console.Out.WriteLine line
        exitCode
    | None -> fallbackToFsc fscPath responseFile

/// Asks a running host to exit. Used before rebuilding the tool, since a warm host keeps its own
/// executable locked. Silent and successful when no host is running.
let runShutdown () =
    let workDir = workDirFor Environment.ProcessPath
    if hostIsAlive workDir then
        try
            File.WriteAllText(Path.Combine(workDir, "shutdown"), "")
            printfn "FlaxFSharpCompiler: shutdown requested"
        with _ -> ()
    else printfn "FlaxFSharpCompiler: no host running"
    ExitOk

[<EntryPoint>]
let main argv =
    match argv with
    | [| "--serve"; workDir |] -> runServer workDir
    | [| "--shutdown" |] -> runShutdown ()
    | [| responseFile |] -> runClient responseFile ""
    | [| responseFile; fscPath |] -> runClient responseFile fscPath
    | _ ->
        eprintfn "usage: FlaxFSharpCompiler <fsc-response-file> [<fsc.dll path for fallback>]"
        eprintfn "       FlaxFSharpCompiler --serve <work-dir>"
        eprintfn "       FlaxFSharpCompiler --shutdown"
        ExitProtocolError
