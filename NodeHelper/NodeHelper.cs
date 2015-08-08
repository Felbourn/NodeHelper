using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CIT_Util;
using CIT_Util.Types;
using UnityEngine;
using Toolbar;

namespace NodeHelper
{
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class NodeHelperAddon : MonoBehaviour
    {
        private const int CleanupInterval = 30;
        private const string ZeroVector = "0.0,0.0,0.0";
        private const byte Transparency = 150;
        private const string TransShader = "Transparent/Diffuse";
        private const bool PrintAdvancedConfig = false;
        private readonly Color _nodeColor = Utilities.GetColorFromRgb(36, 112, 29, Transparency);
        private readonly Color _planeColor = Utilities.GetColorFromRgb(36, 112, 29, Transparency - 25);
        private readonly Color _selectedNodeColor = Utilities.GetColorFromRgb(38, 233, 18, Transparency);
        private HashSet<Part> _affectedParts;
        private short _axisLockCounter;
        private int _cleanupCounter;
        private bool _initialized;
        private bool _inputLockSet;
        private string _newNodeName = "newNode";
        private string _newNodePos = ZeroVector;
        private IButton _nodeHelperButton;
        private Dictionary<AttachNode, GameObject> _nodeMapping;
        private Material _nodeMaterial;
        private Dictionary<AttachNode, string> _nodeNameMapping;
        private string _nodeOrientationCust = ZeroVector;
        private Dictionary<AttachNode, Vector3> _nodePosBackup;
        private float _planeRadius = 0.625f;
        private string _planeRadiusString = "0.625";
        private GameObject[] _planes;
        private bool _printingActive;
        private AttachNode _selectedNode;
        private Part _selectedPart;
        private bool[] _selectedPartRules;
        private bool _show;
        private bool _showCreateMenu;
        private bool[] _showPlanes;
        private float _stepWidth = 0.1f;
        private string _stepWidthString = "0.1";
        private string _targetPos = ZeroVector;
        private Rect _windowPos = new Rect(400, 100, 160, 40);
        private GameObject _orientationPointer;
        
        private static Vector3 GetGoScaleForNode(AttachNode attachNode)
        {
            return ((Vector3.one*attachNode.radius)*(attachNode.size != 0 ? attachNode.size : 0.5f));
        }

        private void HandleActionMenuClosed(Part data)
        {
            if (this._selectedPart != null)
            {
                this._selectedPart.SetHighlightDefault();
            }
            this._clearMapping();
        }

        private void HandleActionMenuOpened(Part data)
        {
            if (data != null)
            {
                if (this._selectedPart != null && data != this._selectedPart)
                {
                    this.HandleActionMenuClosed(null);
                }
                this._selectedPart = data;
            }
        }

        public void OnDestroy()
        {
            GameEvents.onPartActionUIDismiss.Remove(this.HandleActionMenuClosed);
            GameEvents.onPartActionUICreate.Remove(this.HandleActionMenuOpened);
        }

        public void OnGUI()
        {
            const string inputLock = "CIT_NodeHelper_Lock";
            if (HighLogic.LoadedScene != GameScenes.EDITOR)
            {
                if (this._inputLockSet)
                {
                    InputLockManager.RemoveControlLock(inputLock);
                    this._inputLockSet = false;
                }
                return;
            }
            if (!this._show)
            {
                return;
            }
            this._windowPos = GUILayout.Window(this.GetType().FullName.GetHashCode(), this._windowPos, this.WindowGui, "CIT NodeHelper", GUILayout.Width(200), GUILayout.Height(20));
            if (this._windowPos.IsMouseOverRect())
            {
                InputLockManager.SetControlLock(GlobalConst.GUIWindowLockMaskEditor, inputLock);
                this._inputLockSet = true;
            }
            else if (this._inputLockSet)
            {
                InputLockManager.RemoveControlLock(inputLock);
                this._inputLockSet = false;
            }
        }

        private static ApplicationLauncherButton btnLauncher;
        public void Start()
        {
            ConfigNode settings = GameDatabase.Instance.GetConfigNodes("NodeHelper").FirstOrDefault();
            bool stockToolbar;
            bool blizzyToolbar;
            if (settings != null)
            {
                if (!bool.TryParse(settings.GetValue("StockToolbar"), out stockToolbar))
                    stockToolbar = true;
                if (!bool.TryParse(settings.GetValue("BlizzyToolbar"), out blizzyToolbar))
                    blizzyToolbar = true;
            }
            else
            {
                stockToolbar = true;
                blizzyToolbar = true;
            }
            if (stockToolbar)
                btnLauncher = ApplicationLauncher.Instance.AddModApplication(() => _show = !_show, () => _show = !_show, null, null, null, null, ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH, GameDatabase.Instance.GetTexture("CIT/NodeHelper/Textures/button_icon", false));

            GameEvents.onPartActionUICreate.Add(this.HandleActionMenuOpened);
            GameEvents.onPartActionUIDismiss.Add(this.HandleActionMenuClosed);
            this._selectedPart = null;
            this._selectedNode = null;
            this._show = false;
            this._nodeMapping = new Dictionary<AttachNode, GameObject>();
            this._nodeNameMapping = new Dictionary<AttachNode, string>();
            this._nodePosBackup = new Dictionary<AttachNode, Vector3>();
            this._affectedParts = new HashSet<Part>();
            this._selectedPartRules = new bool[5];
            this._showPlanes = new bool[3];
            this._planes = new GameObject[3];
            this._createPlanes();
 
            _orientationPointer = Utilities.CreatePrimitive(PrimitiveType.Cylinder, _planeColor, new Vector3(0.0625f, 0.25f, 0.0625f), false, false, false, name: "node orientation helper", shader: TransShader);
            var vesselOverlays = (EditorVesselOverlays) FindObjectOfType(typeof(EditorVesselOverlays));
            this._nodeMaterial = vesselOverlays.CoMmarker.gameObject.renderer.material;
            this._nodeMaterial.shader = Shader.Find(TransShader);
            this._initialized = true;
            if (ToolbarManager.Instance == null || !blizzyToolbar)
            {
                return;
            }
            this._nodeHelperButton = ToolbarManager.Instance.add("CIT_NodeHelper", "NodeHelperButton");
            this._nodeHelperButton.TexturePath = "CIT/NodeHelper/Textures/button_icon";
            this._nodeHelperButton.ToolTip = "NodeHelper";
            this._nodeHelperButton.Visibility = new GameScenesVisibility(GameScenes.EDITOR);
            this._nodeHelperButton.OnClick += e => this._show = !this._show;
        }
        
        public void Update()
        {
            var el = EditorLogic.fetch;
            if (el == null)
            {
                return;
            }
            
            if (this._cleanupCounter > 0)
            {
                this._cleanupCounter--;
            }
            else
            {
                this._cleanupCounter = CleanupInterval;
                foreach (var affectedPart in this._affectedParts.Where(affectedPart => this._selectedPart == null || affectedPart != this._selectedPart))
                {
                    affectedPart.SetHighlightDefault();
                }
                foreach (var attachNode in this._nodePosBackup.Keys.Where(posBkup => posBkup == null || posBkup.owner == null).ToList())
                {
                    this._nodePosBackup.Remove(attachNode);
                }
                if (EditorLogic.SelectedPart != null)
                {
                    this._clearMapping();
                    this._cleanSelectedPartSetup();
                }
            }
             
            if (!this._initialized || !this._show || this._selectedPart == null)
            {
                if (this._selectedPart != null && !this._show)
                {
                    this._cleanSelectedPartSetup();
                    this._clearMapping();
                }
                return;
            }
            this._updateMapping();
            this._updateAttachRules();
            this._setupSelectedPart();
            this._processPlanes();

            foreach (var mapping in this._nodeMapping.Select(kv => new {node = kv.Key, go = kv.Value}))
            {
                var localPos = this._selectedPart.transform.TransformPoint(mapping.node.position);
                var goTrans = mapping.go.transform;
                goTrans.position = localPos;
                goTrans.localScale = GetGoScaleForNode(mapping.node);
                goTrans.up = mapping.node.orientation;
                if (this._selectedNode != null && mapping.node == this._selectedNode)
                {
                    _updateGoColor(mapping.go, this._selectedNodeColor);
                }
                else
                {
                    _updateGoColor(mapping.go, this._nodeColor);
                }
            }
        }
 
        private bool _showOrientationPointer;

        protected void WindowGui(int windowID)
        {
            if (this._axisLockCounter > 0)
            {
                this._axisLockCounter--;
            }
            var textFieldWidth = GUILayout.Width(90);
            const int spacing = 3;
            const int bigSpacing = spacing*2;
            var expandWidth = GUILayout.ExpandWidth(true);
            GUILayout.BeginVertical("box");
            if (this._selectedPart == null)
            {
                GUILayout.Label("Please select (right-click) a part.", expandWidth);
                GUILayout.EndVertical();
                GUI.DragWindow();
                return;
            }
            foreach (var node in this._nodeMapping.Keys)
            {
                var isSel = this._selectedNode != null && this._selectedNode == node;
                if (GUILayout.Toggle(isSel, this._getNodeName(node), "Button", expandWidth))
                {
                    this._selectedNode = node;
                }
            }
            if (this._nodeMapping.Keys.Count > 0)
            {
                GUILayout.Space(spacing);
            }
            if (GUILayout.Button("Clear Selection", expandWidth))
            {
                this._selectedNode = null;
            }
            GUILayout.EndVertical();
            if (this._selectedNode != null)
            {
                GUILayout.Space(bigSpacing);
                GUILayout.BeginVertical("box");
                GUILayout.Label("Stepwidth:", textFieldWidth);
                GUILayout.BeginHorizontal();
                this._stepWidthString = GUILayout.TextField(this._stepWidthString, textFieldWidth);
                GUILayout.Space(spacing);
                if (GUILayout.Button("Set"))
                {
                    this._parseStepWidth();
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(spacing);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("X+"))
                {
                    this._moveNode(MoveDirs.X, true);
                }
                if (GUILayout.Button("Y+"))
                {
                    this._moveNode(MoveDirs.Y, true);
                }
                if (GUILayout.Button("Z+"))
                {
                    this._moveNode(MoveDirs.Z, true);
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("X-"))
                {
                    this._moveNode(MoveDirs.X, false);
                }
                if (GUILayout.Button("Y-"))
                {
                    this._moveNode(MoveDirs.Y, false);
                }
                if (GUILayout.Button("Z-"))
                {
                    this._moveNode(MoveDirs.Z, false);
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(spacing);
                GUILayout.BeginVertical();
                var cPos = this._selectedNode.position;
                var posText = string.Format("{0},{1},{2}", _formatNumberForOutput(cPos.x), _formatNumberForOutput(cPos.y), _formatNumberForOutput(cPos.z));
                GUILayout.Label("Current Position:", expandWidth);
                GUILayout.BeginHorizontal();
                GUILayout.Label("(" + posText + ")", expandWidth);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("copy to set pos.", expandWidth))
                {
                    this._targetPos = posText;
                }
                GUILayout.EndVertical();
                GUILayout.Space(spacing);
                GUILayout.BeginHorizontal();
                this._targetPos = GUILayout.TextField(this._targetPos, textFieldWidth);
                GUILayout.Space(spacing);
                if (GUILayout.Button("Set to pos", expandWidth))
                {
                    this._setToPos();
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginVertical();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Reset node", expandWidth))
                {
                    this._resetCurrNode();
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Update Reset position", expandWidth))
                {
                    this._updateResetPosCurrNode();
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(spacing);

                GUILayout.BeginVertical();
                GUILayout.Label("curr. node size: " + this._selectedNode.size, expandWidth);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("reduce size", expandWidth))
                {
                    if (this._selectedNode.size > 0)
                    {
                        this._selectedNode.size -= 1;
                    }
                }
                GUILayout.Space(spacing);
                if (GUILayout.Button("increase size", expandWidth))
                {
                    if (this._selectedNode.size < int.MaxValue - 1)
                    {
                        this._selectedNode.size += 1;
                    }
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(spacing);
                GUILayout.BeginVertical();
                var or = this._selectedNode.orientation;
                var orientationString = _formatNumberForOutput(or.x) + "," + _formatNumberForOutput(or.y) + "," + _formatNumberForOutput(or.z);
                GUILayout.Label("curr. node orientation: " + orientationString, expandWidth);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("X", expandWidth))
                {
                    this._selectedNode.orientation = new Vector3(1f, 0f, 0f);
                }
                if (GUILayout.Button("Y", expandWidth))
                {
                    this._selectedNode.orientation = new Vector3(0f, 1f, 0f);
                }
                if (GUILayout.Button("Z", expandWidth))
                {
                    this._selectedNode.orientation = new Vector3(0f, 0f, 1f);
                }
                this._nodeOrientationCust = GUILayout.TextField(this._nodeOrientationCust, expandWidth);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("p. up", expandWidth))
                {
                    this._selectedNode.orientation = this._selectedPart.transform.up;
                }
                if (GUILayout.Button("p. forward", expandWidth))
                {
                    this._selectedNode.orientation = this._selectedPart.transform.forward;
                }
                if (GUILayout.Button("p. right", expandWidth))
                {
                    this._selectedNode.orientation = this._selectedPart.transform.right;
                }
                if (GUILayout.Button("cust.", expandWidth))
                {
                    this._orientNodeToCust();
                }
                GUILayout.EndHorizontal();
                this._showOrientationPointer = GUILayout.Toggle(this._showOrientationPointer, "Show Orientation Pointer", "Button", expandWidth);
                GUILayout.EndVertical();
                GUILayout.Space(bigSpacing);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Delete node", expandWidth))
                {
                    this._deleteCurrNode();
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(bigSpacing);
            }
            else
            {
                GUILayout.Space(bigSpacing);
                GUILayout.BeginVertical("box");
                GUILayout.Label("Part Attach Rules: " + this._getSelPartAttRulesString());
                var tempArr = new bool[5];
                Array.Copy(this._selectedPartRules, tempArr, 5);
                GUILayout.BeginHorizontal();
                tempArr[0] = GUILayout.Toggle(tempArr[0], "stack", "Button", expandWidth);
                tempArr[1] = GUILayout.Toggle(tempArr[1], "srfAttach", "Button", expandWidth);
                tempArr[2] = GUILayout.Toggle(tempArr[2], "allowStack", "Button", expandWidth);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                tempArr[3] = GUILayout.Toggle(tempArr[3], "allowSrfAttch", "Button", expandWidth);
                tempArr[4] = GUILayout.Toggle(tempArr[4], "allowCollision", "Button", expandWidth);
                GUILayout.EndHorizontal();
                this._processAttachRules(tempArr);
                GUILayout.EndVertical();
                GUILayout.Space(bigSpacing);
                if (!this._showCreateMenu)
                {
                    GUILayout.BeginVertical("box");
                    if (GUILayout.Button("Show Node Creation"))
                    {
                        this._showCreateMenu = true;
                    }
                    GUILayout.EndVertical();
                }
                else
                {
                    GUILayout.BeginVertical("box");
                    if (GUILayout.Button("Hide Node Creation"))
                    {
                        this._showCreateMenu = false;
                    }
                    GUILayout.Label("Warning: When adding new nodes and attaching parts to them the vessel can't be launched any more!", GUILayout.ExpandWidth(false));
                    GUILayout.Space(spacing);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Add a node", expandWidth);
                    GUILayout.Space(spacing);
                    if (GUILayout.Button("Create"))
                    {
                        this._createNewNode();
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(spacing);
                    GUILayout.Label("Node name:", expandWidth);
                    this._newNodeName = GUILayout.TextField(this._newNodeName, textFieldWidth);
                    GUILayout.Label("Node pos. rel. to trans.:", expandWidth);
                    this._newNodePos = GUILayout.TextField(this._newNodePos, textFieldWidth);
                    GUILayout.EndVertical();
                    GUILayout.Space(bigSpacing);
                }
            }
            GUILayout.BeginVertical("box");
            GUILayout.Label("Normal planes:", expandWidth);
            GUILayout.BeginHorizontal();
            this._showPlanes[0] = GUILayout.Toggle(this._showPlanes[0], "X", "Button");
            this._showPlanes[1] = GUILayout.Toggle(this._showPlanes[1], "Y", "Button");
            this._showPlanes[2] = GUILayout.Toggle(this._showPlanes[2], "Z", "Button");
            GUILayout.EndHorizontal();
            GUILayout.Label("Plane radius:");
            GUILayout.BeginHorizontal();
            this._planeRadiusString = GUILayout.TextField(this._planeRadiusString, textFieldWidth);
            GUILayout.Space(spacing);
            if (GUILayout.Button("Set"))
            {
                this._parsePlaneRadius();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(bigSpacing);
            GUILayout.BeginVertical("box");
            GUILayout.Label("Write new config:", expandWidth);
            if (GUILayout.Button("Write node data"))
            {
                if (this._selectedPart != null && !this._printingActive)
                {
                    this._printingActive = true;
                    this._printNodeConfigForPart(!PrintAdvancedConfig);
                }
            }
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void _cleanSelectedPartSetup()
        {
            if (this._selectedPart == null)
            {
                return;
            }
            this._selectedPart.SetHighlightDefault();
        }

        private void _clearMapping(bool deselect = true)
        {
            if (!this._initialized)
            {
                return;
            }
            this._orientationPointer.SetActive(false);
            foreach (var kv in this._nodeMapping)
            {
                Destroy(kv.Value);
            }
            this._nodeMapping.Clear();
            this._nodeNameMapping.Clear();
            //this._nodePosBackup.Clear();
            for (var i = 0; i < 3; i++)
            {
                this._showPlanes[i] = false;
                this._planes[i].SetActive(false);
            }
            if (deselect)
            {
                this._selectedPart = null;
                this._selectedNode = null;
                this._selectedPartRules = new bool[5];
            }
        }

        private List<CIT_Util.Types.Tuple<string, string>> _constructNodeValues()
        {
            var nameList = new List<AttachNode>(this._selectedPart.attachNodes.Count + 1);
            nameList.AddRange(this._selectedPart.attachNodes);
            var retList = _uniquifyNames(nameList).Select(attachNode => _nodeToString(attachNode.Key, attachNode.Value)).ToList();
            if (this._selectedPart.srfAttachNode != null)
            {
                retList.Add(_nodeToString(this._selectedPart.srfAttachNode, string.Empty, false));
            }
            return retList;
        }

        private void _createNewNode()
        {
            try
            {
                if (string.IsNullOrEmpty(this._newNodeName))
                {
                    OSD.PostMessageUpperCenter("[NH] name for new node emtpy");
                    return;
                }
                var pos = KSPUtil.ParseVector3(this._newNodePos);
                var an = new AttachNode
                         {
                             owner = this._selectedPart,
                             position = pos,
                             nodeType = AttachNode.NodeType.Stack,
                             size = 1,
                             id = this._findUniqueId(this._newNodeName),
                             attachMethod = AttachNodeMethod.FIXED_JOINT,
                             nodeTransform = this._selectedPart.transform,
                             orientation = this._selectedPart.transform.up
                         };
                this._selectedPart.attachNodes.Add(an);
                this._clearMapping(false);
                OSD.PostMessageUpperCenter("[NH] new node created");
            }
            catch (Exception e)
            {
                Debug.Log("[NH] creating new node threw exception: " + e.Message);
                OSD.PostMessageUpperCenter("[NH] unable to create node, please check vector format");
            }
        }

        private void _createPlanes()
        {
            for (var i = 0; i < 3; i++)
            {
                this._planes[i] = Utilities.CreatePrimitive(PrimitiveType.Cube, this._planeColor, new Vector3(1f, 1f, 1f), false, false, false, shader: TransShader);
            }
        }

        private void _deleteCurrNode()
        {
            if (this._selectedNode != null)
            {
                this._selectedPart.attachNodes.Remove(this._selectedNode);
                this._clearMapping(false);
                OSD.PostMessageUpperCenter("[NH] node deleted");
            }
        }

        private string _findUniqueId(string newNodeName)
        {
            if (this._nodeNameMapping.Keys.All(k => k.id != newNodeName))
            {
                return newNodeName;
            }
            var sameNameCount = this._nodeNameMapping.Keys.Count(k => k.id.Contains(newNodeName));
            sameNameCount++;
            return newNodeName + sameNameCount;
        }

        /// <summary>
        ///     Finds the precision of a float up to approx. 5 digits which is fine for this context.
        /// </summary>
        /// <param name="inNr"></param>
        /// <returns>position of last signif. position after 0</returns>
        private static int _floatPrecision(float inNr)
        {
            inNr = Mathf.Abs(inNr);
            var cnt = 0;
            while (inNr%1 > 0)
            {
                cnt++;
                inNr *= 10;
            }
            return cnt;
        }

        private static string _formatNumberForOutput(float inputNumber)
        {
            var precision = Mathf.Clamp(_floatPrecision(inputNumber), 1, 5);
            var formatString = "{0:F" + precision + "}";
            var trimmedString = string.Format(formatString, inputNumber).TrimEnd('0');
            if (trimmedString[trimmedString.Length - 1] == '.' || trimmedString[trimmedString.Length - 1] == ',')
            {
                trimmedString = trimmedString + "0";
            }
            return trimmedString;
        }

        private string _getNodeName(AttachNode node)
        {
            if (this._nodeNameMapping.ContainsKey(node))
            {
                return this._nodeNameMapping[node];
            }
            return "n.a.";
        }

        private string _getSelPartAttRulesString()
        {
            var sb = new StringBuilder(9);
            for (var i = 0; i < 5; i++)
            {
                var val = this._selectedPartRules[i] ? 1 : 0;
                sb.Append(val);
                if (i < 4)
                {
                    sb.Append(",");
                }
            }
            return sb.ToString();
        }

        private void _moveNode(MoveDirs moveDir, bool positive)
        {
            if (this._selectedNode == null)
            {
                Debug.Log("[NH] no node selected");
                return;
            }
            var debugtext = new StringBuilder(5);
            debugtext.Append(this._getNodeName(this._selectedNode));
            var newPos = this._selectedNode.position;
            var sw = positive ? this._stepWidth : this._stepWidth*-1f;
            debugtext.Append(sw);
            debugtext.Append(" into ");
            switch (moveDir)
            {
                case MoveDirs.X:
                {
                    newPos = new Vector3(newPos.x + sw, newPos.y, newPos.z);
                    debugtext.Append("x position");
                }
                    break;
                case MoveDirs.Y:
                {
                    newPos = new Vector3(newPos.x, newPos.y + sw, newPos.z);
                    debugtext.Append("y position");
                }
                    break;
                default:
                {
                    newPos = new Vector3(newPos.x, newPos.y, newPos.z + sw);
                    debugtext.Append("z position");
                }
                    break;
            }
            Debug.Log(debugtext.ToString());
            this._setToPos(newPos);
        }

        private static CIT_Util.Types.Tuple<string, string> _nodeToString(AttachNode node, string id, bool stack = true)
        {
            const string delim = ", ";

            string retKey;
            if (stack)
            {
                retKey = "node_stack_" + id;
            }
            else
            {
                retKey = "node_attach";
            }
            var sb = new StringBuilder();
            var pos = node.position;
            var or = node.orientation;
            sb.Append(_formatNumberForOutput(pos.x));
            sb.Append(delim);
            sb.Append(_formatNumberForOutput(pos.y));
            sb.Append(delim);
            sb.Append(_formatNumberForOutput(pos.z));
            sb.Append(delim);
            sb.Append(_formatNumberForOutput(or.x));
            sb.Append(delim);
            sb.Append(_formatNumberForOutput(or.y));
            sb.Append(delim);
            sb.Append(_formatNumberForOutput(or.z));
            if (node.size >= 0)
            {
                sb.Append(delim);
                sb.Append(node.size);
            }
            return new CIT_Util.Types.Tuple<string, string>(retKey, sb.ToString());
        }

        private static string _normalizePartName(string messedupName)
        {
            var delim = new[] {"(Clone"};
            var parts = messedupName.Split(delim, StringSplitOptions.None);
            return parts[0];
        }

        private void _orientNodeToCust()
        {
            if (this._selectedNode == null)
            {
                return;
            }
            try
            {
                var custOr = KSPUtil.ParseVector3(this._nodeOrientationCust);
                this._selectedNode.orientation = custOr;
            }
            catch (Exception)
            {
                OSD.PostMessageUpperCenter("[NH] unable to set node orientation, please check vector format");
            }
        }

        private void _parsePlaneRadius()
        {
            float pr;
            if (float.TryParse(this._planeRadiusString, out pr))
            {
                pr = Mathf.Abs(pr);
            }
            this._planeRadius = pr;
            this._planeRadiusString = _formatNumberForOutput(pr);
        }

        private void _parseStepWidth()
        {
            var psw = 0f;
            float sw;
            if (float.TryParse(this._stepWidthString, out sw))
            {
                psw = Mathf.Abs(sw);
            }
            this._stepWidth = psw;
            this._stepWidthString = _formatNumberForOutput(psw);
        }

        private void _printNodeConfigForPart(bool simple = false)
        {
            try
            {
                var normName = _normalizePartName(this._selectedPart.name);
                var cfg = GameDatabase.Instance.root.AllConfigs.Where(c => c.name == normName).Select(c => c).FirstOrDefault();
                if (cfg != null && !string.IsNullOrEmpty(cfg.url))
                {
                    var oldConf = cfg.config;
                    oldConf.RemoveValuesStartWith("node_");
                    var newConf = oldConf;
                    var nodeAttributes = this._constructNodeValues();
                    foreach (var nodeValue in nodeAttributes)
                    {
                        newConf.AddValue(nodeValue.Item1, nodeValue.Item2);
                    }
                    var cfgurl = _stripUrl(cfg.url, cfg.name);
                    var path = KSPUtil.ApplicationRootPath + "/GameData/" + cfgurl + "_NH.cfg";
                    if (!simple)
                    {
                        File.WriteAllText(path, newConf.ToString());
                    }
                    else
                    {
                        var text = new StringBuilder();
                        foreach (var nodeAttribute in nodeAttributes)
                        {
                            text.Append(nodeAttribute.Item1);
                            text.Append(" = ");
                            text.Append(nodeAttribute.Item2);
                            text.Append(Environment.NewLine);
                        }
                        if (_selectedPart != null)
                        {
                            text.Append(Environment.NewLine);
                            text.Append("attachRules = ");
                            text.Append(this._getSelPartAttRulesString());
                            text.Append(Environment.NewLine);
                        }
                        File.WriteAllText(path, text.ToString());
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log("[NH] writing file threw exception: " + e.Message);
            }
            finally
            {
                this._printingActive = false;
            }
        }

        private void _processAttachRules(bool[] tempArr)
        {
            if (this._selectedPart == null || this._selectedPart.attachRules == null)
            {
                return;
            }
            var arr = this._selectedPartRules;
            var pr = this._selectedPart.attachRules;
            if (arr[0] != tempArr[0])
            {
                pr.stack = !pr.stack;
            }
            if (arr[1] != tempArr[1])
            {
                pr.srfAttach = !pr.srfAttach;
            }
            if (arr[2] != tempArr[2])
            {
                pr.allowStack = !pr.allowStack;
            }
            if (arr[3] != tempArr[3])
            {
                pr.allowSrfAttach = !pr.allowSrfAttach;
            }
            if (arr[4] != tempArr[4])
            {
                pr.allowCollision = !pr.allowCollision;
            }
        }

        private void _processPlanes()
        {
            var center = this._selectedPart.transform.position;
            var up = this._selectedPart.transform.up;
            if (this._selectedNode != null)
            {
                center = this._selectedPart.transform.TransformPoint(this._selectedNode.position);
                up = this._selectedNode.orientation;
                _positionOrientationPointer(center, up);
            }
            else
            {
                this._orientationPointer.SetActive(false);
            }
            for (var i = 0; i < 3; i++)
            {
                var plane = this._planes[i];
                var pT = plane.transform;
                if (!this._showPlanes[i])
                {
                    plane.SetActive(false);
                    pT.localScale = new Vector3(1f, 1f, 1f);
                    continue;
                }
                pT.position = center;
                pT.up = up;
                var diameter = this._planeRadius*2f;
                pT.localScale = new Vector3(diameter, 0.025f, diameter);
                var rotVec = Vector3.zero;
                switch (i)
                {
                    case 2:
                        rotVec = new Vector3(90f, 0f, 0f);
                        break;
                    case 0:
                        rotVec = new Vector3(0f, 0f, 90f);
                        break;
                }
                pT.Rotate(rotVec);
                plane.SetActive(true);
            }
        }

        private void _positionOrientationPointer(Vector3 center, Vector3 up)
        {
            this._orientationPointer.transform.up = up;
            this._orientationPointer.transform.position = center;
            this._orientationPointer.transform.Translate(0f, 0.25f, 0f);
            if (_showOrientationPointer)
            {
                this._orientationPointer.SetActive(true);
            }
            else
            {
                this._orientationPointer.SetActive(false);
            }
        }

        private void _resetCurrNode()
        {
            if (this._nodePosBackup.ContainsKey(this._selectedNode))
            {
                this._setToPos(this._nodePosBackup[this._selectedNode]);
                OSD.PostMessageUpperCenter("[NH] node position reset");
            }
            Debug.Log("[NH] failed to reset node to backup position");
        }

        private void _setToPos()
        {
            try
            {
                var nPos = KSPUtil.ParseVector3(this._targetPos);
                this._setToPos(nPos);
            }
            catch (Exception e)
            {
                Debug.Log("set to pos throw exception: " + e.Message);
                OSD.PostMessageUpperCenter("[NH] unable to set position, please check vector format");
            }
        }

        private void _setToPos(Vector3 newPos)
        {
            var currPos = this._selectedNode.position;
            var delta = newPos - currPos;
            this._selectedNode.position = newPos;
            if (this._selectedNode.attachedPart != null)
            {
                var pPos = this._selectedNode.attachedPart.transform.position;
                this._selectedNode.attachedPart.transform.position = pPos + delta;
            }
        }

        private void _setupSelectedPart()
        {
            if (this._selectedPart == null)
            {
                return;
            }
            this._selectedPart.SetHighlightColor(Color.blue);
            this._selectedPart.SetHighlight(true,false);
        }

        private static string _stripUrl(string url, string stripName)
        {
            var nameLength = stripName.Length;
            var urlLength = url.Length - nameLength - 1;
            return url.Substring(0, urlLength);
        }

        private static Dictionary<AttachNode, string> _uniquifyNames(ICollection<AttachNode> nodes)
        {
            var nameDic = new Dictionary<AttachNode, string>(nodes.Count);
            foreach (var attachNode in nodes)
            {
                var n = attachNode.id;
                var cnt = nameDic.Values.Count(v => v == attachNode.id);
                if (cnt > 0)
                {
                    n = n + "_" + (cnt + 1);
                }
                nameDic.Add(attachNode, n);
            }
            return nameDic;
        }

        private void _updateAttachRules()
        {
            if (this._selectedPart == null || this._selectedPart.attachRules == null)
            {
                return;
            }
            var arr = this._selectedPartRules;
            var pr = this._selectedPart.attachRules;
            arr[0] = pr.stack;
            arr[1] = pr.srfAttach;
            arr[2] = pr.allowStack;
            arr[3] = pr.allowSrfAttach;
            arr[4] = pr.allowCollision;
        }

        private static void _updateGoColor(GameObject go, Color color)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && mr.material != null)
            {
                mr.material.color = color;
            }
        }

        private void _updateMapping()
        {
            foreach (var attachNode in this._selectedPart.attachNodes.Where(an => an != null))
            {
                if (!this._nodeMapping.ContainsKey(attachNode))
                {
                    if (this._selectedPart.srfAttachNode != null && attachNode == this._selectedPart.srfAttachNode)
                    {
                        continue;
                    }
                    var scale = GetGoScaleForNode(attachNode);
                    var go = Utilities.CreatePrimitive(PrimitiveType.Sphere, this._nodeColor, scale, true, false, false, shader: TransShader);
                    go.GetComponent<MeshRenderer>().material = this._nodeMaterial;
                    this._nodeMapping.Add(attachNode, go);
                    this._nodeNameMapping.Add(attachNode, attachNode.id);
                    if (!this._nodePosBackup.ContainsKey(attachNode))
                    {
                        this._nodePosBackup.Add(attachNode, attachNode.position);
                    }
                }
            }
        }

        private void _updateResetPosCurrNode()
        {
            if (this._nodePosBackup.ContainsKey(this._selectedNode))
            {
                this._nodePosBackup[this._selectedNode] = this._selectedNode.position;
                OSD.PostMessageUpperCenter("[NH] node position updated");
            }
            Debug.Log("[NH] failed to update node backup position");
        }

        private enum MoveDirs
        {
            X,
            Y,
            Z
        }
    }
}