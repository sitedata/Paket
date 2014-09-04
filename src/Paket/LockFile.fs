﻿/// Contains methods to handle lockfiles.
module Paket.LockFile

open System
open System.IO

/// [omit]
let formatVersionRange (version : VersionRange) = 
    match version with
    | Minimum v -> ">= " + v.ToString()
    | Specific v -> v.ToString()
    | Latest -> ">= 0"
    | Range(_, v1, v2, _) -> ">= " + v1.ToString() + ", < " + v2.ToString()

/// [omit]
let extractErrors (resolved : PackageResolution) = 
    let errors = 
        resolved.ResolvedVersionMap
        |> Seq.map (fun x ->
            match x.Value with
            | Resolved _ -> ""
            | Conflict(c1,c2) ->
                let d1 = c1.Referenced
                let v1 = 
                    match d1.VersionRange with
                    | Specific v -> v.ToString()
                let d2 = c1.Referenced
                let v2 = 
                    match d1.VersionRange with
                    | Specific v -> v.ToString()
                sprintf "%s %s depends on%s  %s (%s)%s%s %s depends on%s  %s (%s)" 
                        d1.Name v1 Environment.NewLine c1.Referenced.Name (formatVersionRange c1.Referenced.VersionRange) Environment.NewLine 
                        d2.Name v2 Environment.NewLine c2.Referenced.Name (formatVersionRange c2.Referenced.VersionRange) 
            )
        |> Seq.filter ((<>) "")
    String.Join(Environment.NewLine,errors)


/// [omit]
let format (resolved : PackageResolution) = 
    let sources = 
        resolved.ResolvedVersionMap
        |> Seq.map (fun x ->
            match x.Value with
            | Resolved d -> 
                match d.Referenced.VersionRange with
                | Specific v -> d.Referenced.Source,d.Referenced,v
            | Conflict(c1,c2) ->
                traceErrorfn "%A %A" c1 c2
                failwith ""
            )
        |> Seq.groupBy (fun (s,_,_) -> s)

    let all = 
        [ yield "NUGET"
          for source, packages in sources do
              yield "  remote: " + source
              yield "  specs:"
              for _, package, version in packages do
                  yield sprintf "    %s (%s)" package.Name (version.ToString()) 
                  for d in resolved.DirectDependencies.[package.Name,version.ToString()] do
                      yield sprintf "      %s (%s)" d.Name (formatVersionRange d.VersionRange)]
    
    String.Join(Environment.NewLine, all)

let private (|Remote|Package|Dependency|Spec|Header|Blank|) (line:string) =
    match line.Trim() with
    | "NUGET" -> Header
    | _ when String.IsNullOrWhiteSpace line -> Blank
    | trimmed when trimmed.StartsWith "remote:" -> Remote (trimmed.Substring(trimmed.IndexOf(": ") + 2))
    | trimmed when trimmed.StartsWith "specs:" -> Spec
    | trimmed when line.StartsWith "      " -> Dependency (trimmed.Split ' ' |> Seq.head)
    | trimmed -> Package trimmed

/// Parses a lockfile from lines
let Parse(lines : string seq) =
    (("http://nuget.org/api/v2", []), lines)
    ||> Seq.fold(fun (currentSource, packages) line ->
        match line with
        | Remote newSource -> newSource, packages
        | Header | Spec | Blank -> (currentSource, packages)
        | Package details ->
            let parts = details.Split(' ')
            let version = parts.[1].Replace("(", "").Replace(")", "")
            currentSource, { SourceType = Nuget
                             Source = currentSource 
                             Name = parts.[0]
                             DirectDependencies = []
                             ResolverStrategy = Max
                             VersionRange = VersionRange.Exactly version } :: packages
        | Dependency details ->
            match packages with
            | currentPackage :: otherPackages -> 
                currentSource,
                { currentPackage with
                    DirectDependencies = [details]
                                         |> List.append currentPackage.DirectDependencies } :: otherPackages
            | _ -> failwith "cannot set a dependency - no package has been specified.")
    |> snd
    |> List.rev

/// Analyzes the dependencies from the packageFile.
let Create(force,packageFile) = 
    let cfg = Config.ReadFromFile packageFile
    cfg.Resolve(force,Nuget.NugetDiscovery)

/// Updates the lockfile with the analyzed dependencies from the packageFile.
let Update(force, packageFile, lockFile) = 
    let resolution = Create(force,packageFile)
    let errors = extractErrors resolution
    if errors = "" then
        File.WriteAllText(lockFile, format resolution)
        printfn "Lockfile written to %s" lockFile
    else 
        traceErrorfn "%s" errors
        failwith "Could not resolve dependencies" // TODO: better error handling
