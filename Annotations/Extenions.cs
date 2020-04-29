using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

    public static class Extensions {
        // internal static void Bind<T>(this object item, Expression<Func<T, object>> expression) where T : NetworkedEntity {
        //     GameObject.FindObjectOfType<BindingSystem>().Bind(item as T, expression);
        // }

        internal static object GetValue(this MemberInfo info, object entity) {
            switch (info.MemberType) {
                case MemberTypes.Property:
                    var prop = info as PropertyInfo;
                    if (null != prop && prop.CanWrite)
                        return prop.GetValue(entity);
                    break;
                case MemberTypes.Field:
                    var field = info as FieldInfo;
                    if (null != field) {
                        return field.GetValue(entity);
                    }
                    break;
            }
            return null;
        }

        internal static bool UpdateValue(this MemberInfo info, object entity, object value) {
            switch (info.MemberType) {
                case MemberTypes.Property:
                    var prop = info as PropertyInfo;
                    if (null != prop && prop.CanWrite)
                        prop.SetValue(entity, value, null);
                    break;
                case MemberTypes.Field:
                    var field = info as FieldInfo;
                    if (null != field) {
                        field.SetValue(entity, value);
                    }
                    break;
                default:
                    return false;
            }
            return true;
        }
    }