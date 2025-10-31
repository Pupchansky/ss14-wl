using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace Content.Server._WL.GameObjects.Systems;

/// <summary>
/// Система для глубокого копирования сущностей.
/// Копируются только <see cref="DataFieldAttribute"/>-члены компонентов.
/// </summary>
public sealed partial class EntityCopySystem : EntitySystem
{
    // TODO: копирование runtime-членов?....

    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private static readonly Type[] ProhibitedDataFieldTypes =
        [
            typeof(EntityUid),
            typeof(IEnumerable<EntityUid>),
            typeof(EntityUid[])
        ];

    public override void Initialize()
    {
        base.Initialize();

    }

    #region Public api
    /// <summary>
    /// Копирует сущность в указанные <see cref="MapCoordinates"/>.
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="coordinates">Координаты для спавна копии.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    public EntityUid? CopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        MapCoordinates coordinates,
        Angle rotation = default,
        bool initialize = true
        )
    {
        TryCopyEntity(sourceEntity, coordinates, out var copy, rotation, initialize);
        return copy;
    }

    /// <summary>
    /// Копирует сущность в указанные <see cref="EntityCoordinates"/>.
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="coordinates">Координаты для спавна копии.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    public EntityUid? CopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        EntityCoordinates coordinates,
        Angle rotation = default,
        bool initialize = true
        )
    {
        TryCopyEntity(sourceEntity, coordinates, out var copy, rotation, initialize);
        return copy;
    }

    /// <summary>
    /// Копирует сущность без привязки к карте (<see cref="MapCoordinates.Nullspace"/>).
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    public EntityUid? CopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        Angle rotation = default,
        bool initialize = false
        )
    {
        TryCopyEntity(sourceEntity, MapCoordinates.Nullspace, out var copy, rotation, initialize);
        return copy;
    }

    /// <summary>
    /// Пытается скопировать сущность в указанные <see cref="MapCoordinates"/>.
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="mapCoordinates">Координаты для спавна копии.</param>
    /// <param name="copiedEntity"><see cref="EntityUid"/> скопированной сущности.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    /// <returns>
    /// <see langword="true"/> - если копирование прошло успешно, <see langword="false"/> - если нет.
    /// </returns>
    public bool TryCopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        MapCoordinates mapCoordinates,
        [NotNullWhen(true)] out EntityUid? copiedEntity,
        Angle rotation = default,
        bool initialize = true
        )
    {
        return TryCopyEntityInternal(sourceEntity, (proto) =>
        {
            return EntityManager.CreateEntityUninitialized(proto.ID, mapCoordinates, null, rotation);
        }, out copiedEntity, initialize);
    }

    /// <summary>
    /// Пытается скопировать сущность в указанные <see cref="EntityCoordinates"/>.
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="entCoordinates">Координаты для спавна копии.</param>
    /// <param name="copiedEntity"><see cref="EntityUid"/> скопированной сущности.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    /// <returns>
    /// <see langword="true"/> - если копирование прошло успешно, <see langword="false"/> - если нет.
    /// </returns>
    public bool TryCopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        EntityCoordinates entCoordinates,
        [NotNullWhen(true)] out EntityUid? copiedEntity,
        Angle rotation = default,
        bool initialize = true
        )
    {
        return TryCopyEntityInternal(sourceEntity, (proto) =>
        {
            return EntityManager.CreateEntityUninitialized(proto.ID, entCoordinates, null, rotation);
        }, out copiedEntity, initialize);
    }

    /// <summary>
    /// Может ли сущность быть скопирована?
    /// </summary>
    public bool CanCopyEntity(EntityUid entity)
    {
        // TODO: нужно протестировать и возможно потом дополнить логикой какой-нибудь.
        return true;
    }
    #endregion

    #region Private stuff
    private Dictionary<Type, Component> GetComps(EntityUid ent)
    {
        var comps = AllComps(ent)
            .Where(c => c is not MetaDataComponent && c is not TransformComponent) /// строка 590 <see cref="EntityManager.RemoveComponentDeferred(EntityUid, Component)"/>
            .Where(c => c is not ActorComponent) // на всякий случай :despair:
            .Select(c => (Component)c)
            .ToDictionary(k => k.GetType(), v => v);

        return comps;
    }

    private void EnsureDataFields(Component origin, Component copy)
    {
        const BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.IgnoreCase |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        var type = origin.GetType();

        foreach (var field in type.GetFields(flags))
        {
            if (field.GetCustomAttribute<DataFieldAttribute>() == null)
                continue;

            if (IsAssignableFromProhib(field))
                continue;

            var originValue = field.GetValue(origin);
            var copyValue = field.GetValue(copy);

            if (Equals(originValue, copyValue))
                continue;

            _serialization.CopyTo<object?>(originValue, ref copyValue);
        }

        foreach (var prop in type.GetProperties(flags))
        {
            if (!prop.CanWrite)
                continue;

            if (prop.GetCustomAttribute<DataFieldAttribute>() == null)
                continue;

            if (IsAssignableFromProhib(prop))
                continue;

            var originValue = prop.GetValue(origin);
            var copyValue = prop.GetValue(copy);

            if (Equals(originValue, copyValue))
                continue;

            _serialization.CopyTo<object?>(originValue, ref copyValue);
        }
    }

    private static bool IsAssignableFromProhib(MemberInfo info)
    {
        var memberType = info switch
        {
            FieldInfo f => f.FieldType,
            PropertyInfo p => p.PropertyType,
            _ => null
        };

        if (memberType == null)
            return false;

        foreach (var type in ProhibitedDataFieldTypes)
        {
            if (type.IsAssignableFrom(memberType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Вся основная логика тут.
    /// Нужен только потому, что <see cref="EntityManager.CreateEntityUninitialized(string?, EntityCoordinates, ComponentRegistry?, Angle)"/>
    /// и <see cref="EntityManager.CreateEntityUninitialized(string?, MapCoordinates, ComponentRegistry?, Angle)"/>
    /// имеют разную логику.
    /// Дешевая по производительности реализация, можно активно использовать.
    /// </summary>
    private bool TryCopyEntityInternal(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        Func<EntityPrototype, EntityUid> getCopyFunc,
        [NotNullWhen(true)] out EntityUid? copiedEntity,
        bool initialize = true
        )
    {
        copiedEntity = null;

        var (origin, originMeta, originXform) = sourceEntity;

        if (!Resolve(origin, ref originMeta, ref originXform, false))
            return false;

        var originProto = originMeta.EntityPrototype;
        if (originProto == null)
            return false;

        var copy = getCopyFunc(originProto);

        // компонент stuff
        var originComps = GetComps(origin);

        var copyComps = GetComps(copy);

        foreach (var (originCompType, _) in originComps)
        {
            if (!copyComps.ContainsKey(originCompType))
            {
                var compUnchecked = _componentFactory.GetComponent(originCompType);
                AddComp(copy, compUnchecked, overwrite: false);
            }
        }

        foreach (var (copyCompType, copyCompInst) in copyComps)
        {
            if (!originComps.TryGetValue(copyCompType, out var originCompInst))
            {
                RemCompDeferred(copy, copyCompInst);
                continue;
            }

            EnsureDataFields(originCompInst, copyCompInst);
        }

        // метадата
        var copyMeta = MetaData(copy);

        _metaData.SetEntityDescription(copy, originMeta.EntityDescription, copyMeta);
        _metaData.SetEntityName(copy, originMeta.EntityName, copyMeta);
        _metaData.SetFlag((copy, copyMeta), originMeta.Flags, enabled: true);

        // (㇏(•̀ᢍ•́)ノ)
        var copyXform = Transform(copy);

        if (originXform.Anchored && !copyXform.Anchored)
            _transform.AnchorEntity((copy, copyXform));
        else if (!originXform.Anchored && copyXform.Anchored)
            _transform.Unanchor(copy, copyXform);

        // иницализация
        if (initialize)
            EntityManager.InitializeAndStartEntity(copy, doMapInit: true);

        copiedEntity = copy;
        return true;
    }
    #endregion
}
