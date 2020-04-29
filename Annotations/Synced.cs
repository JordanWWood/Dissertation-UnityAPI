
[System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field)]
public class Synced : System.Attribute {
    public WorkerType Type;
    
    public Synced(WorkerType type) {
        Type = type;
    }
}