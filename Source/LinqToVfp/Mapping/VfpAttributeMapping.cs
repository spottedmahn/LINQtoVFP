/*
 * LINQ to VFP 
 * http://linqtovfp.codeplex.com/
 * http://www.randomdevnotes.com/tag/linq-to-vfp/
 * 
 * Written by Tom Brothers (TomBrothers@Outlook.com)
 * 
 * Released to the public domain, use at your own risk!
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using IQToolkit;
using IQToolkit.Data.Common;
using IQToolkit.Data.Mapping;

namespace LinqToVfp {
    public class VfpAttributeMapping : VfpAdvancedMapping {
        private Type contextType;
        private Dictionary<string, MappingEntity> entities = new Dictionary<string, MappingEntity>();
        private ReaderWriterLock rwLock = new ReaderWriterLock();

        public VfpAttributeMapping(Type contextType) {
            this.contextType = contextType;
        }

        public override MappingEntity GetEntity(MemberInfo contextMember) {
            Type elementType = TypeHelper.GetElementType(TypeHelper.GetMemberType(contextMember));
            return this.GetEntity(elementType, contextMember.Name);
        }

        public override MappingEntity GetEntity(Type type, string tableId) {
            return this.GetEntity(type, tableId, type);
        }

        private MappingEntity GetEntity(Type elementType, string tableId, Type entityType) {
            MappingEntity entity;
            this.rwLock.AcquireReaderLock(Timeout.Infinite);
            if (!this.entities.TryGetValue(tableId, out entity)) {
                this.rwLock.ReleaseReaderLock();
                this.rwLock.AcquireWriterLock(Timeout.Infinite);
                if (!this.entities.TryGetValue(tableId, out entity)) {
                    entity = this.CreateEntity(elementType, tableId, entityType);
                    this.entities.Add(tableId, entity);
                }

                this.rwLock.ReleaseWriterLock();
            }
            else {
                this.rwLock.ReleaseReaderLock();
            }

            return entity;
        }

        protected virtual IEnumerable<MappingAttribute> GetMappingAttributes(string rootEntityId) {
            var contextMember = this.FindMember(this.contextType, rootEntityId);
            return (MappingAttribute[])Attribute.GetCustomAttributes(contextMember, typeof(MappingAttribute));
        }

        public override string GetTableId(Type entityType) {
            if (this.contextType != null) {
                foreach (var mi in this.contextType.GetMembers(BindingFlags.Instance | BindingFlags.Public)) {
                    FieldInfo fi = mi as FieldInfo;
                    if (fi != null && TypeHelper.GetElementType(fi.FieldType) == entityType) {
                        return fi.Name;
                    }

                    PropertyInfo pi = mi as PropertyInfo;
                    if (pi != null && TypeHelper.GetElementType(pi.PropertyType) == entityType) {
                        return pi.Name;
                    }
                }
            }

            return entityType.Name;
        }

        private MappingEntity CreateEntity(Type elementType, string tableId, Type entityType) {
            if (tableId == null) {
                tableId = this.GetTableId(elementType);
            }

            var members = new HashSet<string>();
            var mappingMembers = new List<AttributeMappingMember>();
            int dot = tableId.IndexOf('.');
            var rootTableId = dot > 0 ? tableId.Substring(0, dot) : tableId;
            var path = dot > 0 ? tableId.Substring(dot + 1) : string.Empty;
            var mappingAttributes = this.GetMappingAttributes(rootTableId);
            var tableAttributes = mappingAttributes.OfType<TableBaseAttribute>()
                .OrderBy(ta => ta.Name);
            var tableAttr = tableAttributes.OfType<TableAttribute>().FirstOrDefault();
            if (tableAttr != null && tableAttr.EntityType != null && entityType == elementType) {
                entityType = tableAttr.EntityType;
            }

            var memberAttributes = mappingAttributes.OfType<MemberAttribute>()
                .Where(ma => ma.Member.StartsWith(path))
                .OrderBy(ma => ma.Member);

            foreach (var attr in memberAttributes) {
                if (string.IsNullOrEmpty(attr.Member)) {
                    continue;
                }

                string memberName = (path.Length == 0) ? attr.Member : attr.Member.Substring(path.Length + 1);
                MemberInfo member = null;
                MemberAttribute attribute = null;
                AttributeMappingEntity nested = null;

                // additional nested mappings
                if (memberName.Contains('.')) {
                    string nestedMember = memberName.Substring(0, memberName.IndexOf('.'));
                    if (nestedMember.Contains('.')) {
                        continue; // don't consider deeply nested members yet
                    }

                    if (members.Contains(nestedMember)) {
                        continue; // already seen it (ignore additional)
                    }

                    members.Add(nestedMember);
                    member = this.FindMember(entityType, nestedMember);
                    string newTableId = tableId + "." + nestedMember;
                    nested = (AttributeMappingEntity)this.GetEntity(TypeHelper.GetMemberType(member), newTableId);
                }
                else {
                    if (members.Contains(memberName)) {
                        throw new InvalidOperationException(string.Format("AttributeMapping: more than one mapping attribute specified for member '{0}' on type '{1}'", memberName, entityType.Name));
                    }

                    member = this.FindMember(entityType, memberName);
                    attribute = attr;
                }

                mappingMembers.Add(new AttributeMappingMember(member, attribute, nested));
            }

            return new AttributeMappingEntity(elementType, tableId, entityType, tableAttributes, mappingMembers);
        }

        private static readonly char[] dotSeparator = new char[] { '.' };

        private MemberInfo FindMember(Type type, string path) {
            MemberInfo member = null;
            string[] names = path.Split(dotSeparator);
            foreach (string name in names) {
                member = type.GetMember(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase).FirstOrDefault();
                if (member == null) {
                    throw new InvalidOperationException(string.Format("AttributMapping: the member '{0}' does not exist on type '{1}'", name, type.Name));
                }

                type = TypeHelper.GetElementType(TypeHelper.GetMemberType(member));
            }

            return member;
        }

        public override string GetTableName(MappingEntity entity) {
            AttributeMappingEntity en = (AttributeMappingEntity)entity;
            var table = en.Tables.FirstOrDefault();
            return this.GetTableName(table);
        }

        private string GetTableName(MappingEntity entity, TableBaseAttribute attr) {
            string name = (attr != null && !string.IsNullOrEmpty(attr.Name))
                ? attr.Name
                : entity.TableId;
            return name;
        }

        public override IEnumerable<MemberInfo> GetMappedMembers(MappingEntity entity) {
            return ((AttributeMappingEntity)entity).MappedMembers;
        }

        public override bool IsMapped(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            return mm != null;
        }

        public override bool IsColumn(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            return mm != null && mm.Column != null;
        }

        public override bool IsComputed(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            return mm != null && mm.Column != null && mm.Column.IsComputed;
        }

        public override bool IsGenerated(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            return mm != null && mm.Column != null && mm.Column.IsGenerated;
        }

        public override bool IsPrimaryKey(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            return mm != null && mm.Column != null && mm.Column.IsPrimaryKey;
        }

        public override string GetColumnName(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            if (mm != null && mm.Column != null && !string.IsNullOrEmpty(mm.Column.Name)) {
                return mm.Column.Name;
            }

            return base.GetColumnName(entity, member);
        }

        public override string GetColumnDbType(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            if (mm != null && mm.Column != null && !string.IsNullOrEmpty(mm.Column.DbType)) {
                return mm.Column.DbType;
            }

            return null;
        }

        public override bool IsAssociationRelationship(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            return mm != null && mm.Association != null;
        }

        public override bool IsRelationshipSource(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            if (mm != null && mm.Association != null) {
                if (mm.Association.IsForeignKey && !typeof(IEnumerable).IsAssignableFrom(TypeHelper.GetMemberType(member))) {
                    return true;
                }
            }

            return false;
        }

        public override bool IsRelationshipTarget(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            if (mm != null && mm.Association != null) {
                if (!mm.Association.IsForeignKey || typeof(IEnumerable).IsAssignableFrom(TypeHelper.GetMemberType(member))) {
                    return true;
                }
            }

            return false;
        }

        public override bool IsNestedEntity(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            return mm != null && mm.NestedEntity != null;
        }

        public override MappingEntity GetRelatedEntity(MappingEntity entity, MemberInfo member) {
            AttributeMappingEntity thisEntity = (AttributeMappingEntity)entity;
            AttributeMappingMember mm = thisEntity.GetMappingMember(member.Name);
            if (mm != null) {
                if (mm.Association != null) {
                    Type elementType = TypeHelper.GetElementType(TypeHelper.GetMemberType(member));
                    Type entityType = (mm.Association.RelatedEntityType != null) ? mm.Association.RelatedEntityType : elementType;
                    return this.GetReferencedEntity(elementType, mm.Association.RelatedEntityID, entityType, "Association.RelatedEntityID");
                }
                else if (mm.NestedEntity != null) {
                    return mm.NestedEntity;
                }
            }

            return base.GetRelatedEntity(entity, member);
        }

        private static readonly char[] separators = new char[] { ' ', ',', '|' };

        public override IEnumerable<MemberInfo> GetAssociationKeyMembers(MappingEntity entity, MemberInfo member) {
            AttributeMappingEntity thisEntity = (AttributeMappingEntity)entity;
            AttributeMappingMember mm = thisEntity.GetMappingMember(member.Name);
            if (mm != null && mm.Association != null) {
                return this.GetReferencedMembers(thisEntity, mm.Association.KeyMembers, "Association.KeyMembers", thisEntity.EntityType);
            }

            return base.GetAssociationKeyMembers(entity, member);
        }

        public override IEnumerable<MemberInfo> GetAssociationRelatedKeyMembers(MappingEntity entity, MemberInfo member) {
            AttributeMappingEntity thisEntity = (AttributeMappingEntity)entity;
            AttributeMappingEntity relatedEntity = (AttributeMappingEntity)this.GetRelatedEntity(entity, member);
            AttributeMappingMember mm = thisEntity.GetMappingMember(member.Name);
            if (mm != null && mm.Association != null) {
                return this.GetReferencedMembers(relatedEntity, mm.Association.RelatedKeyMembers, "Association.RelatedKeyMembers", thisEntity.EntityType);
            }

            return base.GetAssociationRelatedKeyMembers(entity, member);
        }

        private IEnumerable<MemberInfo> GetReferencedMembers(AttributeMappingEntity entity, string names, string source, Type sourceType) {
            return names.Split(separators).Select(n => this.GetReferencedMember(entity, n, source, sourceType));
        }

        private MemberInfo GetReferencedMember(AttributeMappingEntity entity, string name, string source, Type sourceType) {
            var mm = entity.GetMappingMember(name);
            if (mm == null) {
                throw new InvalidOperationException(string.Format("AttributeMapping: The member '{0}.{1}' referenced in {2} for '{3}' is not mapped or does not exist", entity.EntityType.Name, name, source, sourceType.Name));
            }

            return mm.Member;
        }

        private MappingEntity GetReferencedEntity(Type elementType, string name, Type entityType, string source) {
            var entity = this.GetEntity(elementType, name, entityType);
            if (entity == null) {
                throw new InvalidOperationException(string.Format("The entity '{0}' referenced in {1} of '{2}' does not exist", name, source, entityType.Name));
            }

            return entity;
        }

        public override IList<MappingTable> GetTables(MappingEntity entity) {
            return ((AttributeMappingEntity)entity).Tables;
        }

        public override string GetAlias(MappingTable table) {
            return ((AttributeMappingTable)table).Attribute.Alias;
        }

        public override string GetAlias(MappingEntity entity, MemberInfo member) {
            AttributeMappingMember mm = ((AttributeMappingEntity)entity).GetMappingMember(member.Name);
            return (mm != null && mm.Column != null) ? mm.Column.Alias : null;
        }

        public override string GetTableName(MappingTable table) {
            var amt = (AttributeMappingTable)table;
            return this.GetTableName(amt.Entity, amt.Attribute);
        }

        public override bool IsExtensionTable(MappingTable table) {
            return ((AttributeMappingTable)table).Attribute is ExtensionTableAttribute;
        }

        public override string GetExtensionRelatedAlias(MappingTable table) {
            var attr = ((AttributeMappingTable)table).Attribute as ExtensionTableAttribute;
            return (attr != null) ? attr.RelatedAlias : null;
        }

        public override IEnumerable<string> GetExtensionKeyColumnNames(MappingTable table) {
            var attr = ((AttributeMappingTable)table).Attribute as ExtensionTableAttribute;
            if (attr == null) {
                return new string[] { };
            }

            return attr.KeyColumns.Split(separators);
        }

        public override IEnumerable<MemberInfo> GetExtensionRelatedMembers(MappingTable table) {
            var amt = (AttributeMappingTable)table;
            var attr = amt.Attribute as ExtensionTableAttribute;
            if (attr == null) {
                return new MemberInfo[] { };
            }

            return attr.RelatedKeyColumns.Split(separators).Select(n => this.GetMemberForColumn(amt.Entity, n));
        }

        private MemberInfo GetMemberForColumn(MappingEntity entity, string columnName) {
            foreach (var m in this.GetMappedMembers(entity)) {
                if (this.IsNestedEntity(entity, m)) {
                    var m2 = this.GetMemberForColumn(this.GetRelatedEntity(entity, m), columnName);
                    if (m2 != null) {
                        return m2;
                    }
                }
                else if (this.IsColumn(entity, m) && string.Compare(this.GetColumnName(entity, m), columnName, true) == 0) {
                    return m;
                }
            }

            return null;
        }

        public override QueryMapper CreateMapper(QueryTranslator translator) {
            return new VfpAttributeMapper(this, translator);
        }

        private class VfpAttributeMapper : VfpAdvancedMapper {
            private VfpAttributeMapping mapping;

            public VfpAttributeMapper(VfpAttributeMapping mapping, QueryTranslator translator)
                : base(mapping, translator) {
                this.mapping = mapping;
            }
        }

        internal class AttributeMappingMember {
            private MemberInfo member;
            private MemberAttribute attribute;
            private AttributeMappingEntity nested;

            internal AttributeMappingMember(MemberInfo member, MemberAttribute attribute, AttributeMappingEntity nested) {
                this.member = member;
                this.attribute = attribute;
                this.nested = nested;
            }

            internal MemberInfo Member {
                get { return this.member; }
            }

            internal ColumnAttribute Column {
                get { return this.attribute as ColumnAttribute; }
            }

            internal AssociationAttribute Association {
                get { return this.attribute as AssociationAttribute; }
            }

            internal AttributeMappingEntity NestedEntity {
                get { return this.nested; }
            }
        }

        private class AttributeMappingTable : MappingTable {
            private AttributeMappingEntity entity;
            private TableBaseAttribute attribute;

            internal AttributeMappingTable(AttributeMappingEntity entity, TableBaseAttribute attribute) {
                this.entity = entity;
                this.attribute = attribute;
            }

            public AttributeMappingEntity Entity {
                get { return this.entity; }
            }

            public TableBaseAttribute Attribute {
                get { return this.attribute; }
            }
        }

        internal class AttributeMappingEntity : MappingEntity {
            private string tableId;
            private string tableName;
            private Type elementType;
            private Type entityType;
            private ReadOnlyCollection<MappingTable> tables;
            private Dictionary<string, AttributeMappingMember> mappingMembers;

            internal AttributeMappingEntity(Type elementType, string tableId, Type entityType, IEnumerable<TableBaseAttribute> attrs, IEnumerable<AttributeMappingMember> mappingMembers) {
                this.tableId = tableId;
                this.elementType = elementType;
                this.entityType = entityType;
                this.tables = attrs.Select(a => (MappingTable)new AttributeMappingTable(this, a)).ToReadOnly();

                MappingTable mappingTable = this.tables.SingleOrDefault();

                if (mappingTable != null) {
                    this.tableName = ((AttributeMappingTable)mappingTable).Attribute.Name;
                }

                this.mappingMembers = mappingMembers.ToDictionary(mm => mm.Member.Name);
            }

            public override string TableId {
                get { return this.tableId; }
            }

            public string TableName {
                get {
                    if (string.IsNullOrEmpty(this.tableName)) {
                        return this.tableId;
                    }
                    else {
                        return this.tableName;
                    }
                }
            }

            public override Type ElementType {
                get { return this.elementType; }
            }

            public override Type EntityType {
                get { return this.entityType; }
            }

            internal ReadOnlyCollection<MappingTable> Tables {
                get { return this.tables; }
            }

            internal AttributeMappingMember GetMappingMember(string name) {
                AttributeMappingMember mm = null;
                this.mappingMembers.TryGetValue(name, out mm);
                return mm;
            }

            internal IEnumerable<MemberInfo> MappedMembers {
                get { return this.mappingMembers.Values.Select(mm => mm.Member); }
            }
        }
    }
}