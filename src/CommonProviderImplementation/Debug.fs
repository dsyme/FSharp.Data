﻿// --------------------------------------------------------------------------------------
// Helpers for writing type providers
// ----------------------------------------------------------------------------------------------

namespace ProviderImplementation

open System
open System.Collections.Generic
open System.Reflection
open System.Text
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Core.Printf
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Reflection
open ProviderImplementation.ProvidedTypes

module Debug = 

    /// Converts a sequence of strings to a single string separated with the delimiters
    let inline private separatedBy delimiter (items: string seq) = String.Join(delimiter, Array.ofSeq items)

    /// Simulates a real instance of TypeProviderConfig and then creates an instance of the last
    /// type provider added to a namespace by the type provider constructor
    let generate (resolutionFolder: string) (runtimeAssembly: string) typeProviderForNamespacesConstructor args =

        let cfg = new TypeProviderConfig(fun _ -> false)
        let (?<-) cfg prop value =
            cfg.GetType().GetProperty(prop).GetSetMethod(nonPublic = true).Invoke(cfg, [| box value |]) |> ignore
        cfg?ResolutionFolder <- resolutionFolder
        cfg?RuntimeAssembly <- runtimeAssembly
        cfg?ReferencedAssemblies <- Array.zeroCreate<string> 0

        let typeProviderForNamespaces = typeProviderForNamespacesConstructor cfg :> TypeProviderForNamespaces

        let providedTypeDefinition = typeProviderForNamespaces.Namespaces |> Seq.last |> snd |> Seq.last
            
        match args with
        | [||] -> providedTypeDefinition
        | args ->
            let typeName = providedTypeDefinition.Name + (args |> Seq.map (fun s -> ",\"" + (if s = null then "" else s.ToString()) + "\"") |> Seq.reduce (+))
            providedTypeDefinition.MakeParametricType(typeName, args)

    /// Returns a string representation of the signature (and optionally also the body) of all the
    /// types generated by the type provider up to a certain depth and width
    /// If ignoreOutput is true, this will still visit the full graph, but it will output an empty string to be faster
    let prettyPrint signatureOnly ignoreOutput maxDepth maxWidth (t: ProvidedTypeDefinition) = 

        let ns = 
            [ t.Namespace
              "Microsoft.FSharp.Core"
              "Microsoft.FSharp.Core.Operators"
              "Microsoft.FSharp.Collections"
              "Microsoft.FSharp.Control"
              "Microsoft.FSharp.Text" ]
            |> Set.ofSeq

        let pending = new Queue<_>()
        let visited = new HashSet<_>()

        let add t =
            if visited.Add t then
                pending.Enqueue t

        let fullName (t: Type) =
            let fullName = t.Namespace + "." + t.Name
            if fullName.StartsWith "FSI_" then
                fullName.Substring(fullName.IndexOf('.') + 1)
            else
                fullName

        let rec toString useFullName (t: Type) =

            if t = null then
                "<NULL>" // happens in the Freebase provider
            else

                let hasUnitOfMeasure = t.Name.Contains("[")

                let innerToString (t: Type) =
                    match t with
                    | t when t = typeof<bool> -> "bool"
                    | t when t = typeof<obj> -> "obj"
                    | t when t = typeof<int> -> "int"
                    | t when t = typeof<int64> -> "int64"
                    | t when t = typeof<float> -> "float"
                    | t when t = typeof<float32> -> "float32"
                    | t when t = typeof<decimal> -> "decimal"
                    | t when t = typeof<string> -> "string"
                    | t when t = typeof<Void> -> "()"
                    | t when t = typeof<unit> -> "()"
                    | t when t.IsArray -> (t.GetElementType() |> toString useFullName) + "[]"
                    | :? ProvidedTypeDefinition as t ->
                        add t
                        t.Name.Split(',').[0]
                    | t when t.IsGenericType ->            
                        let args =                 
                            if useFullName then
                                t.GetGenericArguments() 
                                |> Seq.map (if hasUnitOfMeasure then (fun t -> t.Name) else toString useFullName)
                            else
                                t.GetGenericArguments() 
                                |> Seq.map (fun _ -> "_")
                        if FSharpType.IsTuple t then
                            separatedBy " * " args
                        elif t.Name.StartsWith "FSharpFunc`" then
                            "(" + separatedBy " -> " args + ")"
                        else 
                          let args = separatedBy "," args
                          let name, reverse = 
                              match t with
                              | t when hasUnitOfMeasure -> toString useFullName t.UnderlyingSystemType, false
                              | t when t.GetGenericTypeDefinition().Name = typeof<int seq>.GetGenericTypeDefinition().Name -> "seq", true
                              | t when t.GetGenericTypeDefinition().Name = typeof<int list>.GetGenericTypeDefinition().Name -> "list", true
                              | t when t.GetGenericTypeDefinition().Name = typeof<int option>.GetGenericTypeDefinition().Name -> "option", true
                              | t when t.GetGenericTypeDefinition().Name = typeof<int ref>.GetGenericTypeDefinition().Name -> "ref", true
                              | t when t.Name = "FSharpAsync`1" -> "async", true
                              | t when ns.Contains t.Namespace -> t.Name, false
                              | t -> (if useFullName then fullName t else t.Name), false
                          let name = name.Split('`').[0]
                          if reverse then
                              args + " " + name 
                          else
                              name + "<" + args + ">"
                    | t when ns.Contains t.Namespace -> t.Name
                    | t when t.IsGenericParameter -> t.Name
                    | t -> if useFullName then fullName t else t.Name

                let rec warnIfWrongAssembly (t:Type) =
                    match t with
                    | :? ProvidedTypeDefinition -> ""
                    | t when t.IsGenericType -> defaultArg (t.GetGenericArguments() |> Seq.map warnIfWrongAssembly |> Seq.tryFind (fun s -> s <> "")) ""
                    | t when t.IsArray -> warnIfWrongAssembly <| t.GetElementType()
                    | t -> if not t.IsGenericParameter && t.Assembly.FullName.Contains "DesignTime" then " [DESIGNTIME]" else ""

                if ignoreOutput then
                    ""
                elif hasUnitOfMeasure || t.IsGenericParameter || t.DeclaringType = null then
                    innerToString t + (warnIfWrongAssembly t)
                else
                    (toString useFullName t.DeclaringType) + "+" + (innerToString t) + (warnIfWrongAssembly t)

        let toSignature (parameters: ParameterInfo[]) =
            if parameters.Length = 0 then
                "()"
            else
                parameters 
                |> Seq.map (fun p -> p.Name + ":" + (toString true p.ParameterType))
                |> separatedBy " -> "

        let printExpr expr =

            let sb = StringBuilder ()
            let print (str:string) = sb.Append(str) |> ignore
        
            let getCurrentIndent() =
                let lastEnterPos = sb.ToString().LastIndexOf('\n')
                if lastEnterPos = -1 then sb.Length + 4 else sb.Length - lastEnterPos - 1

            let breakLine indent = 
                print "\n"
                print (new String(' ', indent))

            let isBigExpression = function
            | Let _ | NewArray _ | NewTuple _ -> true
            | _ -> false

            let inline getAttrs attrName m = 
                ( ^a : (member GetCustomAttributesData : unit -> IList<CustomAttributeData>) m)
                |> Seq.filter (fun attr -> attr.AttributeType.Name = attrName) 

            let inline hasAttr attrName m = 
                not (Seq.isEmpty (getAttrs attrName m))

            let rec printSeparatedByCommas exprs = 
                match exprs with
                | [] -> ()
                | e::es ->
                    printExpr false true e
                    for e in es do
                        print ", "
                        printExpr false true e
                     
            and printCall fromPipe printName (mi:MethodInfo) args = 
                if fromPipe && List.length args = 1 then
                    printName()
                elif not (hasAttr "CompilationArgumentCountsAttribute" mi) then
                    printName()
                    match args with
                    | [] -> print "()"
                    | arg::args ->
                        print "("
                        let indent = getCurrentIndent()
                        printExpr false true arg
                        for arg in args do
                            print ", "
                            if isBigExpression arg then
                                breakLine indent
                            printExpr false true arg
                        print ")"
                else
                    print "("
                    printName()
                    for arg in args do
                      print " "
                      printExpr false true arg
                    print ")"

            and printExpr fromPipe needsParens = function
                | Call (instance, mi, args) ->
                    if mi.Name = "GetArray" && mi.DeclaringType.FullName = "Microsoft.FSharp.Core.LanguagePrimitives+IntrinsicFunctions" then
                        printExpr false true args.Head
                        print ".["
                        printExpr false true args.Tail.Head
                        print "]"
                    elif mi.DeclaringType.IsGenericType && mi.DeclaringType.GetGenericTypeDefinition().Name = typeof<int option>.GetGenericTypeDefinition().Name then
                        if args.IsEmpty then 
                            match instance with
                            | None -> print "None"
                            | Some instance -> 
                                printExpr false true instance
                                print "."
                                print <| mi.Name.Substring("get_".Length)
                        else 
                          print "Some "
                          printExpr false true args.Head
                    elif mi.Name.Contains "." && not args.IsEmpty then
                        // instance method in type extension
                        let printName() = 
                            printExpr false true args.Head
                            print "."
                            print (mi.Name.Substring(mi.Name.IndexOf '.' + 1))
                        printCall fromPipe printName mi args.Tail
                    elif mi.Attributes &&& MethodAttributes.SpecialName = MethodAttributes.SpecialName && mi.Name.StartsWith "get_" && args.IsEmpty then
                        // property get
                        match instance with
                        | Some expr -> printExpr false true expr
                        | None -> print (toString false mi.DeclaringType)
                        print "."
                        print <| mi.Name.Substring("get_".Length)
                    elif mi.Name = "op_PipeRight" && args.Length = 2 then
                        printExpr false false args.Head
                        print " |> "
                        match args.Tail.Head with
                        | Lambda (_, (Call(_,_,_) as call)) -> printExpr true false call
                        | _ as expr -> printExpr false false expr
                    else
                        let printName() =
                            match instance with
                            | Some expr -> printExpr false true expr
                            | None -> print (toString false mi.DeclaringType)
                            print "."
                            print mi.Name
                        let isOptional (arg:Expr, param:ParameterInfo) =
                            hasAttr "OptionalArgumentAttribute" param
                            && arg.ToString() = "Call (None, get_None, [])"
                        let args = 
                            mi.GetParameters()
                            |> List.ofArray 
                            |> List.zip args
                            |> List.filter (not << isOptional)
                            |> List.map fst                        
                        printCall fromPipe printName mi args
                | Let (var1, TupleGet (Var x, 1), Let (var2, TupleGet (Var y, 0), body)) when x = y ->
                    let indent = getCurrentIndent()
                    bprintf sb "let %s, %s = %s" var2.Name var1.Name x.Name
                    breakLine indent
                    printExpr false false body
                | Let (var, value, body) ->
                    let indent = getCurrentIndent()
                    let usePattern = sprintf "IfThenElse(TypeTest(IDisposable,Coerce(%s,Object)),Call(Some(Call(None,UnboxGeneric,[Coerce(%s,Object)])),Dispose,[]),Value(<null>))" var.Name var.Name
                    let body = 
                        match body with
                        | TryFinally (tryExpr, finallyExpr) when finallyExpr.ToString().Replace("\n", null).Replace(" ", null) = usePattern ->
                            bprintf sb "use %s = " var.Name
                            tryExpr
                        | _ -> 
                            if var.IsMutable then
                                bprintf sb "let mutable %s = " var.Name
                            else
                                bprintf sb "let %s = " var.Name
                            body
                    match value with 
                    | Let _ -> 
                        breakLine (indent + 4)
                        printExpr false false value
                    | _ -> printExpr false false value
                    breakLine indent
                    printExpr false false body
                | Value (null, _) ->
                    print "null"
                | Value (value, typ) when typ = typeof<string> && (value :?> string).Contains("\\") ->
                    bprintf sb "@%A" value
                | Value (value, _) ->
                    bprintf sb "%A" value
                | Var (var) ->
                    print var.Name
                | NewObject (ci, args) ->
                    let getSourceConstructFlags (attr:CustomAttributeData) =
                        let arg = attr.ConstructorArguments
                                  |> Seq.filter (fun arg -> arg.ArgumentType.Name = "SourceConstructFlags") 
                                  |> Seq.head
                        arg.Value :?> int
                    let compilationMappings = getAttrs "CompilationMappingAttribute" ci.DeclaringType
                    if not (Seq.isEmpty compilationMappings) && (getSourceConstructFlags (Seq.head compilationMappings)) = int SourceConstructFlags.RecordType then
                        print "{ "
                        let indent = getCurrentIndent()
                        let recordFields = FSharpType.GetRecordFields(ci.DeclaringType)
                        args |> List.iteri (fun i arg ->
                            if i > 0 then
                                breakLine indent
                            print recordFields.[i].Name
                            print " = "
                            printExpr false false arg)
                        print " }"
                    else
                        print "(new "
                        print (toString false ci.DeclaringType)
                        print "("
                        printSeparatedByCommas args
                        print "))"
                | NewDelegate (typ, vars, expr) ->
                    print "new "
                    print (toString false typ)
                    match expr with
                    | Var v when not vars.IsEmpty && vars.Tail.IsEmpty && vars.Head = v -> print "(id)"
                    | _ ->
                        let indent = getCurrentIndent()
                        if vars.IsEmpty then
                            print "(fun () -> "
                        else
                            print "(fun"
                            for var in vars do
                                bprintf sb " (%s:%s)" var.Name (toString false var.Type)
                            print " -> "
                        if isBigExpression expr then
                            breakLine (indent + 4)
                            printExpr false false expr
                        else
                            printExpr false false expr
                    print ")"
                | NewTuple (exprs) ->
                    if needsParens then print "("
                    let indent = getCurrentIndent()
                    printExpr false true exprs.Head
                    for e in exprs.Tail do
                        print ","
                        breakLine indent
                        printExpr false true e
                    if needsParens then print ")"
                | NewArray (_, exprs) ->
                    if exprs.Length = 0 then print "[| |]"
                    else
                        print "[| "
                        let indent = getCurrentIndent()
                        printExpr false true exprs.Head
                        for e in exprs.Tail do
                            breakLine indent
                            printExpr false true e
                        print " |]"
                | Coerce (expr, typ) ->
                    print "("
                    printExpr false false expr
                    print " :> "
                    print (toString false typ)
                    print ")"
                | TupleGet (expr, index) ->
                    print "(let "
                    let rec getTupleLength (typ:Type) =
                        let length = typ.GetGenericArguments().Length
                        if length = 0 then // happens in the Apiary provider                            
                            let typeNameSuffix = typ.Name.Substring(typ.Name.IndexOf('`') + 1)
                            typeNameSuffix.Substring(0, typeNameSuffix.IndexOf('[')) |> Int32.Parse
                        else
                            let lastItem = typ.GetGenericArguments() |> Seq.last
                            if lastItem.Name.StartsWith "Tuple`"
                            then length + getTupleLength lastItem - 1
                            else length
                    let tupleLength = getTupleLength expr.Type
                    let varName = "t" + (string (index + 1))
                    for i in 0..tupleLength-1 do
                        if i = index then
                            print varName
                        else
                            print "_"
                        if i <> tupleLength-1 then
                            print ","
                    print " = "
                    printExpr false false expr
                    print (" in " + varName + ")")
                | expr -> print (expr.ToString())

            printExpr false false expr
            sb.ToString()

        let sb = StringBuilder ()

        let print (str: string) =
            if not ignoreOutput then
                sb.Append(str) |> ignore
        
        let println() =
            if not ignoreOutput then
                sb.AppendLine() |> ignore
              
        let printMember (memberInfo: MemberInfo) =        

            let print str =
                print "    "                
                print str
                println()

            let getMethodBody (m: ProvidedMethod) = 
                seq { if not m.IsStatic then yield (ProvidedTypeDefinition.EraseType m.DeclaringType)
                      for param in m.GetParameters() do yield (ProvidedTypeDefinition.EraseType param.ParameterType) }
                |> Seq.map (fun typ -> Expr.Value(null, typ))
                |> Array.ofSeq
                |> m.GetInvokeCodeInternal false

            let getConstructorBody (c: ProvidedConstructor) = 
                if c.IsImplicitCtor then Expr.Value(()) else
                seq { for param in c.GetParameters() do yield (ProvidedTypeDefinition.EraseType param.ParameterType) }
                |> Seq.map (fun typ -> Expr.Value(null, typ))
                |> Array.ofSeq
                |> c.GetInvokeCodeInternal false

            let printExpr x = 
                if not ignoreOutput then
                    let rec removeParams x = 
                      match x with
                      | Let (_, Value(null, _), body) -> removeParams body
                      | _ -> x
                    let formattedExpr = printExpr (removeParams x)
                    print formattedExpr
                    println()

            let printObj x = 
                if ignoreOutput then 
                    ""
                else 
                    sprintf "\n%O\n" x

            match memberInfo with

            | :? ProvidedConstructor as cons -> 
                if not ignoreOutput then
                    print <| "new : " + 
                             (toSignature <| cons.GetParameters()) + " -> " + 
                             (toString true memberInfo.DeclaringType)
                if not signatureOnly then
                    cons |> getConstructorBody |> printExpr

            | :? ProvidedLiteralField as field -> 
                let value = 
                    if signatureOnly then ""
                    else field.GetRawConstantValue() |> printObj
                if not ignoreOutput then
                    print <| "val " + field.Name + ": " + 
                             (toString true field.FieldType) + 
                             value
                         
            | :? ProvidedProperty as prop -> 
                if not ignoreOutput then
                    print <| (if prop.IsStatic then "static " else "") + "member " + 
                             prop.Name + ": " + (toString true prop.PropertyType) + 
                             " with " + (if prop.CanRead && prop.CanWrite then "get, set" else if prop.CanRead then "get" else "set")
                if not signatureOnly then
                    if prop.CanRead then
                        getMethodBody (prop.GetGetMethod() :?> ProvidedMethod) |> printExpr
                    if prop.CanWrite then
                        getMethodBody (prop.GetSetMethod() :?> ProvidedMethod) |> printExpr

            | :? ProvidedMethod as m ->
                if m.Attributes &&& MethodAttributes.SpecialName <> MethodAttributes.SpecialName then
                    if not ignoreOutput then
                        print <| (if m.IsStatic then "static " else "") + "member " + 
                        m.Name + ": " + (toSignature <| m.GetParameters()) + 
                        " -> " + (toString true m.ReturnType)
                    if not signatureOnly then
                        m |> getMethodBody |> printExpr

            | _ -> ()

        add t

        let currentDepth = ref 0

        while pending.Count <> 0 && !currentDepth <= maxDepth do
            let pendingForThisDepth = new List<_>(pending)
            pending.Clear()
            let pendingForThisDepth = 
                pendingForThisDepth
                |> Seq.sortBy (fun m -> m.Name)
                |> Seq.truncate maxWidth
            for t in pendingForThisDepth do
                match t with
                | t when FSharpType.IsRecord t-> "record "
                | t when FSharpType.IsModule t -> "module "
                | t when t.IsValueType -> "struct "
                | t when t.IsClass && t.IsSealed && t.IsAbstract -> "static class "
                | t when t.IsClass && t.IsAbstract -> "abstract class "
                | t when t.IsClass -> "class "
                | _ -> ""
                |> print
                print (toString true t)
                if t.BaseType <> typeof<obj> then
                    print " : "
                    print (toString true t.BaseType)
                println()
                t.GetMembers() 
                |> Seq.sortBy (fun m -> m.Name)
                |> Seq.iter printMember
                println()
            currentDepth := !currentDepth + 1
    
        sb.ToString()
