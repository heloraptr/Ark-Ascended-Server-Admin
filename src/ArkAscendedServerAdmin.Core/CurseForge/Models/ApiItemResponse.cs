namespace ArkAscendedServerAdmin.CurseForge.Models;

public class ApiItemResponse<T>
{
    public T Data { get; set; } = default!;
}
