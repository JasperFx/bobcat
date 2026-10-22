// Imported from an event model by `bobcat import-event-model`.
//
// A stub has only the fields the model names, typed from what it declares or from its
// example values, plus an Id where the model names no identity. Add the rest as the
// behaviour takes shape. Nothing regenerates this file, so your edits are safe.

using System;

namespace Shop.Ordering;

/// <summary>
/// the aggregate behind PlaceOrder, CancelOrder.
/// </summary>
public class Order { public OrderId Id { get; set; } }
