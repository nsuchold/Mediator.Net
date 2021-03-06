﻿// Licensed to ifak e.V. under one or more agreements.
// ifak e.V. licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ifak.Fast.Json.Linq;

namespace Ifak.Fast.Mediator.Dashboard
{
    [Identify(id: "GenericModuleConfig", bundle: "Generic", path: "generic.html")]
    public class View_GenericConfig : ViewBase
    {
        private ViewConfig configuration = new ViewConfig();

        private readonly Dictionary<string, ClassInfo> objTypes = new Dictionary<string, ClassInfo>();
        private readonly Dictionary<string, EnumInfo> enumTypes = new Dictionary<string, EnumInfo>();
        private readonly Dictionary<string, StructInfo> structTypes = new Dictionary<string, StructInfo>();
        private ObjectInfo[] objects = new ObjectInfo[0];

        private readonly Dictionary<VariableRef, VTQ> mapVariables = new Dictionary<VariableRef, VTQ>();

        public override Task OnActivate() {

            if (Config.NonEmpty) {
                configuration = Config.Object<ViewConfig>();
            }

            return Task.FromResult(true);
        }

        public override async Task<ReqResult> OnUiRequestAsync(string command, DataValue parameters) {

            bool hasModuleID = !(configuration == null || string.IsNullOrEmpty(configuration.ModuleID));
            string moduleID = hasModuleID ? configuration.ModuleID : "IO";

            switch (command) {

                case "GetModel": {

                        objects = await Connection.GetAllObjects(moduleID);

                        mapVariables.Clear();
                        ObjectInfo root = objects.FirstOrDefault(o => !o.Parent.HasValue);
                        VariableValue[] variables = await Connection.ReadAllVariablesOfObjectTree(root.ID);
                        await Connection.EnableVariableValueChangedEvents(SubOptions.AllUpdates(sendValueWithEvent: true), root.ID);

                        foreach (VariableValue vv in variables) {
                            mapVariables[vv.Variable] = vv.Value;
                        }

                        TreeNode node = TransformModel(objects);

                        MetaInfos types = await Connection.GetMetaInfos(moduleID);

                        objTypes.Clear();
                        foreach (ClassInfo ci in types.Classes) {
                            objTypes[ci.FullName] = ci;
                        }
                        enumTypes.Clear();
                        foreach (EnumInfo en in types.Enums) {
                            enumTypes[en.FullName] = en;
                        }
                        structTypes.Clear();
                        foreach (StructInfo sn in types.Structs) {
                            structTypes[sn.FullName] = sn;
                        }

                        JObject typMap = new JObject();
                        foreach (ClassInfo ci in types.Classes) {

                            var members = ci.ObjectMember
                                .Where(m => m.Dimension == Dimension.Array)
                                .Select(m => new {
                                    Array = m.Name,
                                    Type = m.ClassName
                                }).ToArray();

                            var entry = new {
                                ObjectMembers = members
                            };

                            typMap[ci.FullName] = new JRaw(StdJson.ObjectToString(entry));
                        }

                        return ReqResult.OK(new {
                            ObjectTree = node,
                            TypeInfo = typMap
                        });
                    }

                case "GetObject": {

                        GetObjectParams pars = parameters.Object<GetObjectParams>();
                        var values = await GetObjectMembers(pars.ID, pars.Type);

                        ClassInfo info = objTypes[pars.Type];
                        var childTypes = info.ObjectMember
                            .GroupBy(om => om.ClassName)
                            .Select(g => new ChildType() {
                                TypeName = g.Key,
                                Members = g.Select(x => x.Name).ToArray()
                            }).ToList();

                        var res = new {
                            ObjectValues = values,
                            ChildTypes = childTypes
                        };
                        return ReqResult.OK(res);
                    }

                case "Save": {

                        SaveParams saveParams = parameters.Object<SaveParams>();

                        foreach (var m in saveParams.Members) {
                            Console.WriteLine(m.Name + " => " + m.Value);
                        }
                        ObjectRef obj = ObjectRef.FromEncodedString(saveParams.ID);
                        MemberValue[] mw = saveParams.Members.Select(m => MemberValue.Make(obj, m.Name, DataValue.FromJSON(m.Value))).ToArray();

                        await Connection.UpdateConfig(mw);

                        objects = await Connection.GetAllObjects(moduleID);
                        TreeNode node = TransformModel(objects);

                        var values = await GetObjectMembers(saveParams.ID, saveParams.Type);
                        return ReqResult.OK(new {
                            ObjectValues = values,
                            ObjectTree = node
                        });
                    }

                case "Delete": {

                        ObjectRef obj = ObjectRef.FromEncodedString(parameters.GetString());
                        await Connection.UpdateConfig(ObjectValue.Make(obj, DataValue.Empty));

                        objects = await Connection.GetAllObjects(moduleID);
                        TreeNode node = TransformModel(objects);
                        return ReqResult.OK(node);
                    }

                case "AddObject": {

                        AddObjectParams addParams = parameters.Object<AddObjectParams>();
                        ObjectRef objParent = ObjectRef.FromEncodedString(addParams.ParentObjID);
                        DataValue dataValue = DataValue.FromObject(new {
                            ID = addParams.NewObjID,
                            Name = addParams.NewObjName
                        });
                        var element = AddArrayElement.Make(objParent, addParams.ParentMember, dataValue);
                        await Connection.UpdateConfig(element);

                        objects = await Connection.GetAllObjects(moduleID);

                        VariableValue[] newVarVals = await Connection.ReadAllVariablesOfObjectTree(ObjectRef.Make(objParent.ModuleID, addParams.NewObjID));
                        foreach (VariableValue vv in newVarVals) {
                            mapVariables[vv.Variable] = vv.Value;
                        }

                        TreeNode node = TransformModel(objects);
                        return ReqResult.OK(new {
                            ObjectID = ObjectRef.Make(moduleID, addParams.NewObjID),
                            Tree = node
                        });
                    }

                case "DragDrop": {

                        DragDropParams dropParams = parameters.Object<DragDropParams>();

                        ObjectRef obj = ObjectRef.FromEncodedString(dropParams.FromID);
                        ObjectValue objValue = await Connection.GetObjectValueByID(obj);

                        var deleteObj = ObjectValue.Make(obj, DataValue.Empty);

                        ObjectRef objParent = ObjectRef.FromEncodedString(dropParams.ToID);

                        var addElement = AddArrayElement.Make(objParent, dropParams.ToArray, objValue.Value);

                        await Connection.UpdateConfig(new ObjectValue[] { deleteObj }, new MemberValue[0], new AddArrayElement[] { addElement } );

                        objects = await Connection.GetAllObjects(moduleID);
                        TreeNode node = TransformModel(objects);
                        return ReqResult.OK(node);
                    }

                case "WriteVariable": {

                        var write = parameters.Object<WriteVariable_Params>();
                        VTQ vtq = new VTQ(Timestamp.Now, Quality.Good, DataValue.FromJSON(write.V));
                        await Connection.WriteVariable(ObjectRef.FromEncodedString(write.ObjID), write.Var, vtq);
                        return ReqResult.OK();
                    }

                case "MoveObject": {

                        var move = parameters.Object<MoveObject_Params>();
                        bool up = move.Up;

                        ObjectRef obj = ObjectRef.FromEncodedString(move.ObjID);
                        ObjectInfo objInfo = await Connection.GetObjectByID(obj);
                        MemberRefIdx? parentMember = objInfo.Parent;

                        if (parentMember.HasValue) {
                            MemberValue value = await Connection.GetMemberValue(parentMember.Value.ToMemberRef());
                            DataValue v = value.Value;
                            if (v.IsArray) {
                                JArray array = (JArray)StdJson.JTokenFromString(v.JSON);
                                int index = parentMember.Value.Index;
                                if (up && index > 0) {

                                    JToken tmp = array[index - 1];
                                    array[index - 1] = array[index];
                                    array[index] = tmp;

                                    MemberValue mv = MemberValue.Make(parentMember.Value.ToMemberRef(), DataValue.FromObject(array));
                                    await Connection.UpdateConfig(mv);
                                }
                                else if (!up && index < array.Count - 1) {

                                    JToken tmp = array[index + 1];
                                    array[index + 1] = array[index];
                                    array[index] = tmp;

                                    MemberValue mv = MemberValue.Make(parentMember.Value.ToMemberRef(), DataValue.FromObject(array));
                                    await Connection.UpdateConfig(mv);
                                }
                            }
                        }

                        objects = await Connection.GetAllObjects(moduleID);
                        TreeNode node = TransformModel(objects);
                        return ReqResult.OK(node);
                    }

                case "Browse": {

                        var browse = parameters.Object<Browse_Params>();

                        var m = MemberRef.Make(ObjectRef.FromEncodedString(browse.ObjID), browse.Member);
                        BrowseResult res = await Connection.BrowseObjectMemberValues(m);
                        return ReqResult.OK(res.Values.Select(d => d.GetString()));

                    }
                case "GetNewID": {
                        string type = parameters.GetString();
                        string id = GetObjectID(type);
                        return ReqResult.OK(id);
                    }

                default:
                    return ReqResult.Bad("Unknown command: " + command);
            }
        }

        public override void OnVariableValueChanged(VariableValue[] variables) {

            var changes = new List<VarChange>(variables.Length);

            for (int n = 0; n < variables.Length; ++n) {
                VariableValue vv = variables[n];
                mapVariables[vv.Variable] = vv.Value;
                changes.Add(new VarChange() {
                    ObjectID = vv.Variable.Object.ToString(),
                    VarName = vv.Variable.Name,
                    V = vv.Value.V,
                    T = vv.Value.T,
                    Q = vv.Value.Q
                });
            }
            Context.SendEventToUI("VarChange", changes);
        }

        private async Task<List<ObjectMember>> GetObjectMembers(string id, string type) {

            ObjectRef obj = ObjectRef.FromEncodedString(id);
            ClassInfo info = objTypes[type];

            MemberRef[] members = info.SimpleMember.Select(m => MemberRef.Make(obj, m.Name)).ToArray();
            MemberValue[] memValues = await Connection.GetMemberValues(members);

            var values = new List<ObjectMember>();

            for (int i = 0; i < info.SimpleMember.Count; ++i) {
                SimpleMember m = info.SimpleMember[i];
                MemberValue v = memValues[i];
                string defaultValue = "";
                if (m.DefaultValue.HasValue && m.Dimension != Dimension.Array) {
                    defaultValue = m.DefaultValue.Value.JSON;
                }
                else if (m.Type == DataType.Struct) {
                    defaultValue = StdJson.ObjectToString(GetStructDefaultValue(m), indented: true, ignoreNullValues: false);
                    //Console.WriteLine("=> " + m.Name + ": " + defaultValue);
                }
                else {
                    defaultValue = DataValue.FromDataType(m.Type, 1).JSON;
                }
                var member = new ObjectMember() {
                    Key = obj.ToEncodedString() + "__" + m.Name,
                    Name = m.Name,
                    Type = m.Type.ToString(),
                    IsScalar = m.Dimension == Dimension.Scalar,
                    IsOption = m.Dimension == Dimension.Optional,
                    IsArray = m.Dimension == Dimension.Array,
                    Category = m.Category,
                    Browseable = m.Browseable,
                    Value = new JRaw(v.Value.JSON),
                    ValueOriginal = new JRaw(v.Value.JSON),
                    EnumValues = ResolveEnum(m),
                    StructMembers = ResolveStruct(m),
                    DefaultValue = defaultValue
                };
                values.Add(member);
            }
            return values;
        }

        private StructMember[] ResolveStruct(SimpleMember m) {
            if (m.Type != DataType.Struct) return null;
            string structName = m.TypeConstraints;
            StructInfo structInfo = structTypes[structName];
            return structInfo.Member.Select(sm => new StructMember() {
                Name = sm.Name,
                Type = sm.Type.ToString(),
                IsScalar = sm.Dimension == Dimension.Scalar,
                IsOption = sm.Dimension == Dimension.Optional,
                IsArray = sm.Dimension == Dimension.Array,
                EnumValues = ResolveEnum(sm),
                StructMembers = ResolveStruct(sm)
            }).ToArray();
        }

        private JObject GetStructDefaultValue(SimpleMember m) {
            if (m.Type != DataType.Struct) return null;
            string structName = m.TypeConstraints;
            StructInfo structInfo = structTypes[structName];
            JObject obj = new JObject();
            foreach (var sm in structInfo.Member) {
                string dv;
                if (sm.DefaultValue.HasValue) {
                    dv = sm.DefaultValue.Value.JSON;
                }
                else if (sm.Dimension == Dimension.Optional) {
                    dv = "null";
                }
                else {
                    dv = DataValue.FromDataType(sm.Type, 1).JSON;
                }
                obj[sm.Name] = new JRaw(dv);
            }
            return obj;
        }

        private string[] ResolveEnum(SimpleMember m) {
            if (m.Type != DataType.Enum) return null;
            string enumName = m.TypeConstraints;
            EnumInfo enumInfo = enumTypes[enumName];
            return enumInfo.Values.Select(ev => ev.Description).ToArray();
        }

        private string GetObjectID(string type) {

            ClassInfo info = objTypes[type];

            string prefix = info.IdPrefix + "_";
            int prefixLen = prefix.Length;

            int maxN = 0;

            foreach (ObjectInfo obj in objects) {
                if (obj.ClassName == type) {
                    string localID = obj.ID.LocalObjectID;
                    if (localID.StartsWith(prefix)) {
                        string num = localID.Substring(prefixLen);
                        int n;
                        if (int.TryParse(num, out n)) {
                            if (n > maxN) {
                                maxN = n;
                            }
                        }
                    }
                }
            }
            return prefix + (maxN + 1).ToString("000");
        }

        //////////////////////////////////////////

        public class ObjectMember
        {
            public string Key { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public bool IsScalar { get; set; }
            public bool IsOption { get; set; }
            public bool IsArray { get; set; }
            public string Category { get; set; } = "";
            public bool Browseable { get; set; }
            public string[] BrowseValues { get; set; } = new string[0];
            public JToken Value { get; set; }
            public JToken ValueOriginal { get; set; }
            public string[] EnumValues { get; set; }
            public StructMember[] StructMembers { get; set; }
            public string DefaultValue { get; set; }
        }

        public class StructMember
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool IsScalar { get; set; }
            public bool IsOption { get; set; }
            public bool IsArray { get; set; }
            public string[] EnumValues { get; set; }
            public StructMember[] StructMembers { get; set; }
        }

        //////////////////////////////////////////

        public class GetObjectParams
        {
            public string ID { get; set; }
            public string Type { get; set; }
        }

        public class SaveParams
        {
            public string ID { get; set; }
            public string Type { get; set; }
            public SaveMember[] Members { get; set; }
        }

        public class SaveMember
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public class AddObjectParams
        {
            public string ParentObjID { get; set; }
            public string ParentMember { get; set; }
            public string NewObjID { get; set; }
            public string NewObjType { get; set; }
            public string NewObjName { get; set; }
        }

        public class DragDropParams
        {
            public string FromID { get; set; }
            public string ToID { get; set; }
            public string ToArray { get; set; }
        }

        public class WriteVariable_Params
        {
            public string ObjID { get; set; }
            public string Var { get; set; }
            public string V { get; set; }
        }

        public class MoveObject_Params
        {
            public string ObjID { get; set; }
            public bool Up { get; set; }
        }

        public class Browse_Params
        {
            public string ObjID { get; set; }
            public string Member { get; set; }
        }

        public class ChildType
        {
            public string TypeName { get; set; } = "";
            public string[] Members { get; set; } = new string[0];
        }

        private TreeNode TransformModel(ObjectInfo[] objects) {

            ObjectInfo rootObjInfo = null;
            var objectsChildren = new Dictionary<ObjectRef, List<ObjectInfo>>();

            foreach (ObjectInfo obj in objects) {
                var parent = obj.Parent;
                if (parent.HasValue) {
                    var key = parent.Value.Object;
                    if (!objectsChildren.ContainsKey(key)) {
                        objectsChildren[key] = new List<ObjectInfo>();
                    }
                    objectsChildren[key].Add(obj);
                }
                else {
                    rootObjInfo = obj;
                }
            }

            return MapObjectInfo2TreeNode(rootObjInfo, null, objectsChildren);
        }

        private TreeNode MapObjectInfo2TreeNode(ObjectInfo obj, List<ObjectInfo> siblings, Dictionary<ObjectRef, List<ObjectInfo>> map) {
            List<TreeNode> children = null;
            if (map.ContainsKey(obj.ID)) {
                var ch = map[obj.ID];
                children = ch.Select(n => MapObjectInfo2TreeNode(n, ch, map)).ToList();
            }
            else {
                children = new List<TreeNode>();
            }

            var listVariables = new List<VariableVal>();
            foreach (Variable v in obj.Variables) {
                var key = VariableRef.Make(obj.ID, v.Name);
                VTQ vtq;
                if (mapVariables.TryGetValue(key, out vtq)) {
                    listVariables.Add(new VariableVal() {
                        Name = v.Name,
                        V = vtq.V,
                        T = vtq.T,
                        Q = vtq.Q
                    });
                }
            }

            int count = 1;
            int idx = 0;

            if (obj.Parent.HasValue) {
                var p = obj.Parent.Value;
                idx = p.Index;
                string mem = p.Name;
                count = siblings.Count(sib => sib.Parent.Value.Name == mem);
            }

            return new TreeNode() {
                ID = obj.ID.ToString(),
                ParentID = obj.Parent.HasValue ? obj.Parent.Value.Object.ToString() : "",
                First = idx == 0,
                Last = idx + 1 == count,
                Name = obj.Name,
                Type = obj.ClassName,
                Children = children,
                Variables = listVariables
            };
        }

        public class ViewConfig
        {
            public string ModuleID { get; set; }
        }
    }

    public class TreeNode
    {
        public string ID { get; set; } = "";
        public string ParentID { get; set; } = "";
        public bool First { get; set; } = false;
        public bool Last { get; set; } = false;
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public List<VariableVal> Variables { get; set; } = new List<VariableVal>();
        public List<TreeNode> Children { get; set; } = new List<TreeNode>();
    }

    public class VariableVal
    {
        public string Name { get; set; }
        public DataValue V { get; set; }
        public Timestamp T { get; set; }
        public Quality   Q { get; set; }
    }

    public class VarChange
    {
        public string ObjectID { get; set; }
        public string VarName { get; set; }
        public DataValue V { get; set; }
        public Timestamp T { get; set; }
        public Quality Q { get; set; }
    }
}
