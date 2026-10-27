using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.CameraControl;
using EFT.UI;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// Adds options to EFT's own interaction menu while the player looks at a target (the PaP machine's approach, kept
    /// here so the Thundergun needs no other mod): the game shows them and runs the selected one on the interact key.
    /// An option without an action is shown greyed out.
    /// </summary>
    internal class ThundergunInteraction
    {
        private const float CheckInterval = 0.1f;

        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private List<InteractionAction> _actions = new List<InteractionAction>();
        private readonly List<InteractionAction> _stale = new List<InteractionAction>();
        private string _signature;
        private HideoutPlayerOwner _owner;
        private bool _looking;
        private float _nextCheck;

        // The hideout camera isn't tagged MainCamera, so Camera.main is unreliable there
        private static Camera ActiveCamera => CameraManager.Exist ? CameraManager.Instance.Camera : Camera.main;

        public void Tick(HideoutPlayerOwner owner, Transform target, float distance, Func<IEnumerable<(string name, Action action)>> options)
        {
            // The raycast and option list only need refreshing a few times a second
            if (Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + CheckInterval;
                _looking = target != null && !owner.IsGuest && owner.FirstPersonMode && LookingAt(owner, target, distance);
                if (_looking)
                    Rebuild(options().ToList());
            }

            if (!_looking || _actions.Count == 0)
            {
                Clear();
                return;
            }

            _owner = owner;
            AvailableInteractionState current = owner.AvailableInteractionState.Value;
            if (current == null || !_actions.All(current.Actions.Contains))
            {
                var menu = new AvailableInteractionState();
                if (current?.Actions != null)
                    menu.Actions.AddRange(current.Actions.Where(a => !IsOurs(a)));
                menu.Actions.AddRange(_actions);
                menu.InitSelected();
                owner.AvailableInteractionState.Value = menu;
            }
            _stale.Clear();
        }

        private void Rebuild(List<(string name, Action action)> options)
        {
            string signature = string.Join("|", options.Select(o => o.name + (o.action == null ? "-" : "+")));
            if (signature == _signature)
                return;
            _stale.AddRange(_actions);
            _actions = options.Select(o => new InteractionAction
            {
                Name = o.name,
                Action = o.action ?? (() => { }),
                Disabled = o.action == null,
            }).ToList();
            _signature = signature;
        }

        private bool IsOurs(InteractionAction action) => _actions.Contains(action) || _stale.Contains(action);

        public void Clear()
        {
            if (_owner != null)
            {
                AvailableInteractionState current = _owner.AvailableInteractionState.Value;
                if (current?.Actions != null && current.Actions.Any(IsOurs))
                {
                    var menu = new AvailableInteractionState();
                    menu.Actions.AddRange(current.Actions.Where(a => !IsOurs(a)));
                    menu.InitSelected();
                    _owner.AvailableInteractionState.Value = menu;
                }
            }
            _owner = null;
            _signature = null;
            _actions.Clear();
            _stale.Clear();
        }

        public void Reset()
        {
            Clear();
            _looking = false;
        }

        private bool LookingAt(HideoutPlayerOwner owner, Transform target, float distance)
        {
            Camera camera = ActiveCamera;
            if (camera == null)
                return false;

            Transform player = ((Component)owner.HideoutPlayer).transform;
            Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            int count = Physics.RaycastNonAlloc(ray, _hits, distance, ~0, QueryTriggerInteraction.Ignore);

            RaycastHit? nearest = null;
            for (int i = 0; i < count; i++)
            {
                if (_hits[i].collider.transform.IsChildOf(player))
                    continue;
                if (!nearest.HasValue || _hits[i].distance < nearest.Value.distance)
                    nearest = _hits[i];
            }
            return nearest.HasValue && nearest.Value.collider.transform.IsChildOf(target);
        }
    }
}
