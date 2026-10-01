<%@ Page Language="C#" AutoEventWireup="true" CodeBehind="Default.aspx.cs" Inherits="Legacy.Web._Default" %>

<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml">
<head runat="server">
    <title>Legacy Orders</title>
</head>
<body>
    <h1><%: Greeting() %></h1>
    <form id="form1" runat="server">
        <asp:Label ID="lblTotal" runat="server" />
        <asp:Button ID="btnCalculate" runat="server" Text="Calculate" OnClick="btnCalculate_Click" />
    </form>
</body>
</html>
