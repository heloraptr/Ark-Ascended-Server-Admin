namespace ArkAscendedServerAdmin.CurseForge.Models;

public class ApiListResponse<T>
{
    public List<T> Data { get; set; } = [];
    public Pagination? Pagination { get; set; }
}