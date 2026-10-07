namespace Bobcat.Tests.FSharpTypes

open System

type OrderId = OrderId of Guid

type Status =
    | Pending = 0
    | Shipped = 1

type Address = { Street: string; City: string }

type ShipmentConfirmed =
    { OrderId: OrderId
      TrackingNumber: string
      Carrier: string option
      Weight: decimal
      Status: Status
      Notes: string list
      Tags: Set<string>
      ShippedTo: Address
      ShippedAt: DateTimeOffset }

[<CLIMutable>]
type MutableShipment = { Id: Guid; Name: string; Count: int }

type Shipment(id: Guid, name: string) =
    member _.Id = id
    member _.Name = name
    member val Count = 0 with get, set

type Discount = { Code: string; Percent: decimal voption }

type Payment =
    | Card of number: string
    | Cash

/// Partial objects authored in F#: lambdas convert to the Expression arguments of With.
module Authoring =
    open Bobcat

    let trackingOnly () =
        Specifications
            .Specify<ShipmentConfirmed>()
            .With((fun x -> x.TrackingNumber), "1Z999")
            .With((fun x -> x.ShippedTo.City), "Austin")
            .Build()

    let mutableShipment () =
        Specifications.Specify<MutableShipment>().With((fun x -> x.Count), 3).Build()
