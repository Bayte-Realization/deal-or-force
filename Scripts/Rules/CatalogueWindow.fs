// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

/// An overflow-safe slice of a catalogue. No collection or UI state is retained.
[<Struct>]
type CatalogueSlice =
    { Page: int
      PageCount: int
      Start: int
      Count: int }

/// Shared value-only window calculation for bounded presentation lists.
[<AbstractClass; Sealed>]
type CatalogueWindow private () =
    static member Plan(total: int, pageSize: int, requestedPage: int) : CatalogueSlice =
        let total = System.Math.Max(0, total)
        let size = System.Math.Clamp(pageSize, 1, 100)
        let pages = int (System.Math.Max(1L, (int64 total + int64 size - 1L) / int64 size))
        let page = System.Math.Clamp(requestedPage, 0, pages - 1)
        let start = int (int64 page * int64 size)
        { Page = page; PageCount = pages; Start = start
          Count = System.Math.Min(size, total - start) }
