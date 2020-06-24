ResGen was copied from [PowerShell/PowerShell](https://github.com/PowerShell/PowerShell/tree/master/src/ResGen).

In this case ResGen is used to process resource file `ODataUtils\src\PowerShell.Cmdletization.OData\resources\Resources.resx`
and to generate `ODataUtils\src\PowerShell.Cmdletization.OData\gen\Resources.cs` that is used in the build.

For any modification to resources:

1. make required changes in Resources.resx using a text editor;
2. generate updated Resources.cs:
  1. `cd ODataUtils\src\ResGen`
  2. `dotnet run`
3. compile as usual using `build.ps1`
4. check in updated `Resources.resx` and `Resources.cs`

[More info on ResGen](https://github.com/PowerShell/PowerShell/blob/master/docs/dev-process/resx-files.md).
