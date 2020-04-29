using System;
using System.Linq;
using System.Reflection;
using Grpc.Core;
using me.jordanwood.proto.v1.statemanager;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using Type = me.jordanwood.proto.v1.statemanager.Type;

public class NetworkingSystem : MonoBehaviour {
    private Channel channel;
    private State.StateClient stateClient;

    public NetworkedEntityCoordinator NetworkedEntityCoordinator;
    public Type ClientType;
    
    public delegate void NewEntity(string entityId, WorkerType type);
    public event NewEntity NewClientEvent;

    public WorkerType WorkerType {
        get {
            switch (ClientType) {
                case Type.Worker:
                    return WorkerType.WORKER;
                case Type.Client:
                    return WorkerType.CLIENT;
                case Type.Telemetry:
                    return WorkerType.TELEMETRY;
                default:
                    return WorkerType.UNKNOWN;
            }
        }
    }

    public string Token { get; private set; }
    public TextMeshProUGUI IdText;
    public int TickRate = 20;
    
    protected void Awake() {
        channel = new Channel("127.0.0.1:50051", ChannelCredentials.Insecure);
        stateClient = new State.StateClient(channel);
        
        var result = stateClient.Connect(new InitialConnectionRequest { Type = ClientType });
        Token = result.Token;
        
        if (IdText != null) IdText.text = "ID: " + Token + " | Type: " + ClientType;
        NetworkedEntityCoordinator = new NetworkedEntityCoordinator(WorkerType, stateClient, TickRate, Token);
        NetworkedEntityCoordinator.DataStream();
    }

    protected void Update() {
        while (!NetworkedEntityCoordinator.MarkedForInstantiation.IsEmpty) {
            NetworkedEntityCoordinator.MarkedForInstantiation.TryDequeue(out var item);
            var go = Resources.Load("Prefabs/" + item.PrefabName) as GameObject;
            var entity = go.GetComponent<NetworkedEntity>();
            entity.prefab = go;
            entity.entityId = item.Id;
            entity.prefabName = item.PrefabName;

            Instantiate(go);
        }

        while (!NetworkedEntityCoordinator.MarkedForUpdate.IsEmpty) {
            NetworkedEntityCoordinator.MarkedForUpdate.TryDequeue(out var item);
            (var entity, var packet, var info) = item;

            var val = GetValue(packet.Data, packet.MemberType);
            if (val == null) {
                Debug.LogError("Value is null for some reason");
                continue;
            }
            
            switch (info.MemberType) {
                case MemberTypes.Property:
                    PropertyInfo prop = info as PropertyInfo;
                    if (null != prop && prop.CanWrite)
                        prop.SetValue(entity, val, null);
                    break;
                case MemberTypes.Field:
                    FieldInfo field = info as FieldInfo;
                    if (null != field) {
                        field.SetValue(entity, val);
                    }
                    break;
                default:
                    Debug.LogWarning(
                        $"{entity.gameObject.name}:{nameof(entity)}->{packet.MemberName} does not exist or is not valid member type");
                    break;
            }
        }

        while (!NetworkedEntityCoordinator.ClientConnectEventQueue.IsEmpty) {
            NetworkedEntityCoordinator.ClientConnectEventQueue.TryDequeue(out var item);
            NewClientEvent?.Invoke(item.Item1, item.Item2);
        }
    }

    protected void OnDestroy() {
        Debug.Log("Stopping");
        
        NetworkedEntityCoordinator.Cleanup();
        channel.ShutdownAsync().Wait();
        NetworkedEntityCoordinator.isStopping = true;
    }
    
    private object GetValue(string data, string type) {
        try {
            System.Type valueType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                valueType = assembly.GetType(type);
                if (valueType != null) break;
            }

            if (valueType == null) throw new Exception($"Unknown Type: {type}");
            switch (valueType) {
                default:
                    return JsonConvert.DeserializeObject(data, valueType);
            }
        }
        catch (Exception e) {
            Debug.LogError("Failed to GetValue as object " + e.Message);
            return null;
        }
    }
}
