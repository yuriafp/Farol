Imports Legacy.Core.Orders

' Relies on project-level imports (System.Linq, System.Collections.Generic) declared in the .vbproj,
' the way classic VB projects usually do: there is no Imports statement for them in this file.
Public Class ShippingCalculator
    Private ReadOnly _calculator As OrderCalculator

    Public Sub New(calculator As OrderCalculator)
        _calculator = calculator
    End Sub

    Public Function TotalWithShipping(orderIds As IEnumerable(Of Integer), shipping As Decimal) As Decimal
        Dim totals = orderIds.Select(Function(id) _calculator.GetTotal(id)).ToList()
        Return totals.Sum() + shipping
    End Function
End Class
