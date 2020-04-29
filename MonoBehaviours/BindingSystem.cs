using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Grpc.Core;
using me.jordanwood.proto.v1.statemanager;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using Type = me.jordanwood.proto.v1.statemanager.Type;

public class BindingSystem : MonoBehaviour {
    private NetworkingSystem _networkingSystem;
    private Dictionary<(string, MemberInfo), MemberInfo> _binding;

    public void Bind(string entityId, MemberInfo to, MemberInfo from) {
        var key = (entityId, to);
        if (to != from && !_binding.ContainsKey(key)) _binding.Add(key, from);
    }

    protected void Awake() {
        _networkingSystem = FindObjectOfType<NetworkingSystem>();
        _binding = new Dictionary<(string, MemberInfo), MemberInfo>();
    }

    protected void LateUpdate() {
        foreach (KeyValuePair<(string, MemberInfo),MemberInfo> pair in _binding) {
            if (_networkingSystem.NetworkedEntityCoordinator._entities.TryGetValue(pair.Key.Item1, out var entity))
                pair.Key.Item2.UpdateValue(entity, pair.Value.GetValue(entity));

        }
    }
}
