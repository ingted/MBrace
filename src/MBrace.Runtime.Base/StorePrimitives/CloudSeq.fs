﻿namespace Nessos.MBrace.Runtime.Store

    open System
    open System.IO
    open System.Collections
    open System.Collections.Generic
    open System.Runtime.Serialization

    open Nessos.FsPickler

    open Nessos.MBrace
    open Nessos.MBrace.Core
    open Nessos.MBrace.Utils

    type CloudSeqInfo = { Size : int64; Count : int; Type : Type }

    // TODO: CLOUDSEQINFO CTOR

    [<Serializable>]
    [<StructuredFormatDisplay("{StructuredFormatDisplay}")>] 
    type CloudSeq<'T> (id : string, container : string ) as this =
        let factoryLazy = lazy IoC.Resolve<CloudSeqProvider>()

        let info = lazy (Async.RunSynchronously <| factoryLazy.Value.GetCloudSeqInfo(this))

        interface ICloudSeq with
            member this.Name = id
            member this.Container = container
            member this.Type = info.Value.Type
            member this.Count = info.Value.Count
            member this.Size = info.Value.Size
            member this.Dispose () =
                (factoryLazy.Value :> ICloudSeqProvider).Delete(this)

        interface ICloudSeq<'T>

        override this.ToString () = sprintf "%s - %s" container id

        member private this.StructuredFormatDisplay = this.ToString()

        interface IEnumerable with
            member this.GetEnumerator () = 
                factoryLazy.Value.GetEnumerator(this)
                |> Async.RunSynchronously :> IEnumerator
        
        interface IEnumerable<'T> with
            member this.GetEnumerator () = 
                factoryLazy.Value.GetEnumerator(this)  
                |> Async.RunSynchronously
            
        interface ISerializable with
            member this.GetObjectData (info : SerializationInfo , context : StreamingContext) =
                info.AddValue ("id", (this :> ICloudSeq<'T>).Name)
                info.AddValue ("container", (this :> ICloudSeq<'T>).Container)

        new (info : SerializationInfo , context : StreamingContext) =
            CloudSeq(info.GetString "id", 
                     info.GetString "container")
    
    and CloudSeqProvider (store : IStore, cacheStore : LocalCacheStore) = 

        let pickler = Nessos.MBrace.Runtime.Serializer.Pickler
        let extension = "seq"
        let postfix = fun s -> sprintf' "%s.%s" s extension

        let getInfo (stream : Stream) : CloudSeqInfo =
            let pos = stream.Position
            stream.Seek(int64 -sizeof<int>, SeekOrigin.End) |> ignore
            let br = new BinaryReader(stream)
            let headerSize = br.ReadInt32()
            stream.Seek(int64 -sizeof<int> - int64 headerSize, SeekOrigin.End) |> ignore
            
            let count = br.ReadInt32()
            let ty = pickler.Deserialize<Type> stream
            let size = br.ReadInt64()

            stream.Position <- pos
            { Count = count; Size = size; Type = ty }

        let setInfo (stream : Stream) (info : CloudSeqInfo) =
            let bw = new BinaryWriter(stream)
            let headerStart = stream.Position
            
            bw.Write(info.Count)
            pickler.Serialize(stream, info.Type)
            bw.Write(stream.Position + 2L * int64 sizeof<int>)
            
            let headerEnd = stream.Position
            bw.Write(int(headerEnd - headerStart))

        let getCloudSeqInfo cont id = 
            async {
                let id = postfix id
                use! stream = cacheStore.Read(cont,id)
                return getInfo stream
            }

        member this.GetCloudSeqInfo (cseq : ICloudSeq) : Async<CloudSeqInfo> =
            getCloudSeqInfo cseq.Container cseq.Name

        member this.GetEnumerator<'T> (cseq : ICloudSeq<'T>) : Async<IEnumerator<'T>> =
            async {
                let cont, id, ty = cseq.Container, postfix cseq.Name, cseq.Type
                let! stream = cacheStore.Read(cont, id)

                let info = getInfo stream

                if info.Type <> ty then
                    let msg = sprintf' "CloudSeq type mismatch. Internal type %s, got %s" info.Type.AssemblyQualifiedName ty.AssemblyQualifiedName
                    return raise <| MBraceException(msg)

                return pickler.DeserializeSequence<'T>(stream, info.Count)
            }

            member this.GetIds (container : string) : Async<string []> =
                async {
                    let! files = store.GetFiles(container)
                    return files
                        |> Seq.filter (fun w -> w.EndsWith <| sprintf' ".%s" extension)
                        |> Seq.map (fun w -> w.Substring(0, w.Length - extension.Length - 1))
                        |> Seq.toArray
                }

        interface ICloudSeqProvider with

            // this is wrong: type should not be passed as a parameter: temporary fix
            member this.GetSeq (container, id) = async {
                let! cseqInfo = getCloudSeqInfo container id
                let cloudSeqTy = typedefof<CloudSeq<_>>.MakeGenericType [| cseqInfo.Type |]
                let cloudSeq = Activator.CreateInstance(cloudSeqTy,[| id :> obj ; container :> obj |])
                return cloudSeq :?> ICloudSeq
            }

            member this.Create (items : IEnumerable, container : string, id : string, ty : Type) : Async<ICloudSeq> =
                async {
                    let serializeTo stream = async {
                        let length = pickler.SerializeSequence(ty, stream, items, leaveOpen = true)
                        return setInfo stream { Size = -1L; Count = length; Type = ty }
                    }
                    do! cacheStore.Create(container, postfix id, serializeTo)
                    do! cacheStore.Commit(container, postfix id)

                    let cloudSeqTy = typedefof<CloudSeq<_>>.MakeGenericType [| ty |]
                    let cloudSeq = Activator.CreateInstance(cloudSeqTy, [| id :> obj; container :> obj |])
                
                    return cloudSeq :?> _
                }

            member this.GetSeqs(container : string) : Async<ICloudSeq []> =
                async {
                    let! ids = this.GetIds(container)
                    return 
                        ids |> Seq.map (fun id -> (Async.RunSynchronously(getCloudSeqInfo container id)).Type, container, id)
                            |> Seq.map (fun (t,c,i) ->
                                    let cloudSeqTy = typedefof<CloudSeq<_>>.MakeGenericType [| t |]
                                    let cloudSeq = Activator.CreateInstance(cloudSeqTy, [| i :> obj; c :> obj |])
                                    cloudSeq :?> ICloudSeq)
                            |> Seq.toArray
                }

            member self.Delete(cseq : ICloudSeq) : Async<unit> = 
                store.Delete(cseq.Container, postfix cseq.Name)
