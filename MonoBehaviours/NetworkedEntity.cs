using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using me.jordanwood.proto.v1.bridge;
using me.jordanwood.proto.v1.statemanager;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;

public abstract class NetworkedEntity : MonoBehaviour {
    public string entityId;
    [HideInInspector] public string prefabName;
    [HideInInspector] public string instantiateForClient;
    public GameObject prefab;

    private NetworkingSystem _networking;
    private BindingSystem _bindingSystem;

    public void OnEnable() {
        if (prefabName == "") {
            prefabName = prefab.name;
        }

        var networkingObj = GameObject.FindGameObjectWithTag("Networking");
        _bindingSystem = networkingObj.GetComponent<BindingSystem>();
        _networking = networkingObj.GetComponent<NetworkingSystem>();
        Debug.Log("Registering entity " + gameObject.name);
        _networking.NetworkedEntityCoordinator.RegisterNetworkedEntity(this, entityId);

        StartCoroutine(SubmitTrackedObjects());
    }

    private IEnumerator SubmitTrackedObjects() {
        while (true) {
            _networking.NetworkedEntityCoordinator.SyncTrackedMembers(this);
            yield return new WaitForSeconds(1f / _networking.TickRate);
        }
    }

    protected void Bind<T>(Expression<Func<T, dynamic>> to, Expression<Func<T, dynamic>> from) where T : NetworkedEntity {
        var fromInto = (from.Body as MemberExpression)?.Member ?? (((UnaryExpression) from.Body).Operand as MemberExpression)?.Member ?? null;
        if (fromInto == null) return;
        
        var toInfo = (to.Body as MemberExpression)?.Member ?? (((UnaryExpression) to.Body).Operand as MemberExpression)?.Member ?? null;
        if (toInfo == null) return;
        
        _bindingSystem.Bind(entityId, toInfo, fromInto);
    }

    protected bool HaveAuthority<T>(Expression<Func<T, dynamic>> expression) where T : NetworkedEntity {
        MemberInfo memberInfo =
            (expression.Body as MemberExpression)?.Member ??
            (((UnaryExpression) expression.Body).Operand as MemberExpression)?.Member ?? null;
        if (memberInfo == null) return false;
        if (!memberInfo.IsDefined(typeof(Synced))) return true;
        return memberInfo.GetCustomAttribute<Synced>().Type == _networking.WorkerType &&
               _networking.NetworkedEntityCoordinator.HaveAuthority(entityId, memberInfo);
    }

    public EntityState ToEntityState() {
        var state = new EntityState {
            Id = entityId,
            PrefabName = prefabName
        };

        state.Members.Add(GetSyncedMembers());

        return state;
    }

    public IEnumerable<MemberInfo> Members;

    public IEnumerable<MemberInfo> getSyncedMemberInfos(WorkerType typeFilter = default) {
        if (Members == null) {
            var infos= this.GetType()
                .GetMembers(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            Members = (typeFilter == default
                    ? infos.Where(member => member.IsDefined(typeof(Synced)))
                    : infos.Where(member =>
                        member.IsDefined(typeof(Synced)) &&
                        member.GetCustomAttribute<Synced>().Type == _networking.WorkerType));
        }

        return Members;
    }

    public Member[] GetSyncedMembers() {
        return getSyncedMemberInfos().Select(x => {
            var type = x.GetCustomAttribute<Synced>().Type;

            string auth = _networking.Token;
            if (type == WorkerType.CLIENT && instantiateForClient != "") auth = instantiateForClient;
            else _networking.NetworkedEntityCoordinator.AuthoritativeItems.Add((entityId, x.Name));

            object value = null;
            string memberType = "";
            if (x.MemberType == MemberTypes.Field) {
                var finfo = x as FieldInfo;
                memberType = finfo.FieldType.FullName;
                value = finfo.GetValue(this);
            }
            else if (x.MemberType == MemberTypes.Property) {
                var pinfo = x as PropertyInfo;
                memberType = pinfo.PropertyType.FullName;
                value = pinfo.GetValue(this);
            }

            if (memberType == "")
                return null;

            return new Member {
                AuthoritativeMember = auth,
                MemberName = x.Name,
                MemberType = memberType,
                Data = JsonConvert.SerializeObject(value)
            };
        }).Where(x => x != null).ToArray();
    }
}