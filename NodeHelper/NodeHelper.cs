﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
//using Toolbar;
using KSP.UI.Screens;
using Utils;

namespace Utils
{
    public class Tuple<T1, T2>
    {
        public Tuple() { }
        public Tuple(T1 item1, T2 item2)
        {
            Item1 = item1;
            Item2 = item2;
        }

        public T1 Item1 { get; set; }
        public T2 Item2 { get; set; }
    }

    public static class OSD
    {
        public static void PostMessageLowerRightCorner(string text, float shownFor = 1)
        {
            Debug.Log(text);
        }

        public static void PostMessageUpperCenter(string text, float shownFor = 3.7F)
        {
            Debug.Log(text);
        }
    }

    public static class UI
    {
        public static GameObject CreatePrimitive(PrimitiveType type, Color color, Vector3 scale, bool isActive, bool hasCollider, bool hasRigidbody, bool hasRenderer = true, string name = "", string shader = "Diffuse")
        {
            GameObject obj = GameObject.CreatePrimitive(type);
            var renderer = obj.GetComponent<Renderer>();
            renderer.material.color = color;
            obj.transform.localScale = scale;
            obj.SetActive(isActive);
            obj.name = name;
            renderer.material.shader = Shader.Find(shader);
            obj.layer = 1;
            return obj;
        }

        public static Color GetColorFromRgb(byte r, byte g, byte b, byte a = 255)
        {
            Color c = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            return c;
        }
    }
}

namespace NodeHelper
{
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class NodeHelperAddon : MonoBehaviour
    {
        private const int CleanupInterval = 30;
        private const string ZeroVector = "0.0, 0.0, 0.0";
        private const string TransShader = "Transparent/Diffuse";
        private const bool PrintAdvancedConfig = false;

        private readonly Color _nodeColor = Utils.UI.GetColorFromRgb(0, 226, 94, 150);
        private readonly Color _selectedNodeColor = Utils.UI.GetColorFromRgb(249, 255, 94, 200);
        private readonly Color _planeColor = Utils.UI.GetColorFromRgb(100, 191, 219, 150);
        private readonly Color _orientColor = Utils.UI.GetColorFromRgb(255, 0, 42, 200);

        private HashSet<Part> _affectedParts;

        private Dictionary<AttachNode, GameObject> _nodeMapping;
        private Dictionary<AttachNode, string> _nodeNameMapping;
        private Dictionary<AttachNode, Vector3> _nodePosBackup;

        private IButton _nodeHelperButton;
        private Material _nodeMaterial;
        private AttachNode _selectedNode;
        private Part _selectedPart;
        private ConfigNode _settings;
        private GameObject _orientationPointer;
        private GameObject[] _planes;

        private bool[] _selectedPartRules;
        private bool[] _showPlanes;

        private bool _initialized;
        private bool inputLockActive;
        private bool _printingActive;
        private bool _show;
        private bool _showCreateMenu;
        private bool _stockToolbar;
        private bool _blizzyToolbar;
        private bool _showOrientationPointer = true;
        private bool _show001 = false;
        private float _stepWidth = 0.1f;
        private float _planeRadius = 0.625f;
        private string _stepWidthString = "0.1";
        private string _targetPos = ZeroVector;
        private string _nodeOrientationCust = ZeroVector;
        private string _planeRadiusString = "0.625";
        private string inputLock = "NH_Lock";
        private string _newNodeName = "newNode";
        private string _newNodePos = ZeroVector;
        private int _cleanupCounter;

        private Rect nodeListPos = new Rect(315, 100, 160, 40);
        private Rect windowPos = new Rect(1375, 80, 160, 40);
        private Rect nodeEditPos = new Rect(315, 470, 160, 40);
        
        private static Vector3 GetGoScaleForNode(AttachNode attachNode)
        {
            return Vector3.one * attachNode.radius * (attachNode.size > 0 ? attachNode.size : 0.2f);
        }

        private void HandleActionMenuClosed(Part data)
        {
            if (this._selectedPart != null)
                this._selectedPart.SetHighlightDefault();
            this._clearMapping();
        }

        private void HandleActionMenuOpened(Part data)
        {
            if (data == null)
                return;
            if (this._selectedPart != null && data != this._selectedPart)
                this.HandleActionMenuClosed(null);
            this._selectedPart = data;
        }

        public void OnDestroy()
        {
            GameEvents.onPartActionUIDismiss.Remove(this.HandleActionMenuClosed);
            GameEvents.onPartActionUICreate.Remove(this.HandleActionMenuOpened);
            if (btnLauncher != null)
                ApplicationLauncher.Instance.RemoveModApplication(btnLauncher);
        }

        private void HandleWindow(ref Rect position, int id, string name, GUI.WindowFunction func)
        {
            position = GUILayout.Window(this.GetType().FullName.GetHashCode() + id, position, func, name, GUILayout.Width(200), GUILayout.Height(20));
        }

        public void OnGUI()
        {
            if (HighLogic.LoadedScene != GameScenes.EDITOR)
            {
                if (this.inputLockActive)
                {
                    InputLockManager.RemoveControlLock(this.inputLock);
                    this.inputLockActive = false;
                }
                return;
            }

            if (!this._show)
                return;

            HandleWindow(ref this.nodeListPos, 0, "Node Helper - Node List", this.NodeListGui);
            if (this._selectedPart != null)
                HandleWindow(ref this.windowPos, 1, "Node Helper - Part Data", this.WindowGui);
            if (this._selectedNode != null)
                HandleWindow(ref this.nodeEditPos, 2, "Node Helper - Edit Node", this.NodeEditGui);
        }

        private static ApplicationLauncherButton btnLauncher = null;

        public void Start()
        {
            this._settings = GameDatabase.Instance.GetConfigNodes("NodeHelper").FirstOrDefault();

            if (this._settings != null)
            {
                if (!bool.TryParse(this._settings.GetValue("StockToolbar"), out this._stockToolbar))
                    this._stockToolbar = true;
                if (!bool.TryParse(this._settings.GetValue("BlizzyToolbar"), out this._blizzyToolbar))
                    this._blizzyToolbar = true;

                int coord;
                if (int.TryParse(this._settings.GetNode("ListWindow").GetValue("x"), out coord))
                    this.nodeListPos.x = coord;
                if (int.TryParse(this._settings.GetNode("ListWindow").GetValue("y"), out coord))
                    this.nodeListPos.y = coord;

                if (int.TryParse(this._settings.GetNode("PartWindow").GetValue("x"), out coord))
                    this.windowPos.x = coord;
                if (int.TryParse(this._settings.GetNode("PartWindow").GetValue("y"), out coord))
                    this.windowPos.y = coord;

                if (int.TryParse(this._settings.GetNode("NodeWindow").GetValue("x"), out coord))
                    this.nodeEditPos.x = coord;
                if (int.TryParse(this._settings.GetNode("NodeWindow").GetValue("y"), out coord))
                    this.nodeEditPos.y = coord;

                if (!bool.TryParse(this._settings.GetValue("OrientPointer"), out this._showOrientationPointer))
                    this._showOrientationPointer = true;
                bool.TryParse(this._settings.GetValue("Show001"), out this._showOrientationPointer);
            }
            else
            {
                this._stockToolbar = true;
                this._blizzyToolbar = true;
            }
            if (this._stockToolbar && btnLauncher == null)
                btnLauncher = ApplicationLauncher.Instance.AddModApplication(() => _show = !_show, () => _show = !_show, null, null, null, null, ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH, GameDatabase.Instance.GetTexture("Felbourn/NodeHelper/NodeHelper", false));

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
 
            _orientationPointer = Utils.UI.CreatePrimitive(PrimitiveType.Cylinder, _orientColor, new Vector3(0.01f, 0.15f, 0.01f), false, false, false, name: "Orientation Pointer", shader: TransShader);
            var vesselOverlays = (EditorVesselOverlays) FindObjectOfType(typeof(EditorVesselOverlays));
            this._nodeMaterial = vesselOverlays.CoMmarker.gameObject.GetComponent<Renderer>().material;
            this._nodeMaterial.shader = Shader.Find(TransShader);
            this._initialized = true;
            if (ToolbarManager.Instance == null || !this._blizzyToolbar)
                return;

            this._nodeHelperButton = ToolbarManager.Instance.add("NodeHelper", "NodeHelperButton");
            this._nodeHelperButton.TexturePath = "Felbourn/NodeHelper/NodeHelper";
            this._nodeHelperButton.ToolTip = "NodeHelper";
            this._nodeHelperButton.Visibility = new GameScenesVisibility(GameScenes.EDITOR);
            this._nodeHelperButton.OnClick += (e => this._show = !this._show);
        }

        private bool ShouldBeLocked()
        {
            if (nodeListPos.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)))
                return true;

            if (this._selectedPart != null && windowPos.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)))
                return true;

            if (this._selectedNode != null && nodeEditPos.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)))
                return true;

            return false;
        }

        private void UpdateLock()
        {
            if (ShouldBeLocked())
            {
                if (!inputLockActive)
                {
                    Debug.Log("[NH] lock input");
                    InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, this.inputLock);
                    inputLockActive = true;
                }
            }
            else
            {
                if (inputLockActive)
                {
                    Debug.Log("[NH] unlock input");
                    InputLockManager.RemoveControlLock(this.inputLock);
                    inputLockActive = false;
                }
            }
        }

        public void Update()
        {
            var el = EditorLogic.fetch;
            if (el == null)
                return;

            UpdateLock();

            if (this._cleanupCounter > 0)
            {
                this._cleanupCounter--;
            }
            else
            {
                this._cleanupCounter = CleanupInterval;
                foreach (var affectedPart in this._affectedParts.Where(affectedPart => this._selectedPart == null || affectedPart != this._selectedPart))
                    affectedPart.SetHighlightDefault();
                foreach (var attachNode in this._nodePosBackup.Keys.Where(posBkup => posBkup == null || posBkup.owner == null).ToList())
                    this._nodePosBackup.Remove(attachNode);

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
                    _updateGoColor(mapping.go, this._selectedNodeColor);
                else
                    _updateGoColor(mapping.go, this._nodeColor);
            }
        }
 
        protected void NodeEditGui(int windowID)
        {
            var expandWidth = GUILayout.ExpandWidth(true);

            if (this._selectedNode != null)
                if (GUILayout.Button("Clear Selection", expandWidth))
                    this._selectedNode = null;

            //=====================================================================================
            GUILayout.BeginVertical("box");

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            GUILayout.Label("Step:", GUILayout.Width(90));
            if (GUILayout.Button("1.0"))
            {
                this._stepWidth = 1;
                this._stepWidthString = "1.0";
            }
            if (GUILayout.Button("0.1"))
            {
                this._stepWidth = 0.1f;
                this._stepWidthString = "0.1";
            }
            if (GUILayout.Button("0.01"))
            { 
                this._stepWidth = 0.01f;
                this._stepWidthString = "0.01";
            }
            if (this._show001 && GUILayout.Button("0.001"))
            { 
                this._stepWidth = 0.001f;
                this._stepWidthString = "0.001";
            }
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            this._stepWidthString = GUILayout.TextField(this._stepWidthString, GUILayout.Width(90));
            if (GUILayout.Button("Set"))
                this._parseStepWidth();
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("X+"))
                this._moveNode(MoveDirs.X, true);
            if (GUILayout.Button("Y+"))
                this._moveNode(MoveDirs.Y, true);
            if (GUILayout.Button("Z+"))
                this._moveNode(MoveDirs.Z, true);
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("X-"))
                this._moveNode(MoveDirs.X, false);
            if (GUILayout.Button("Y-"))
                this._moveNode(MoveDirs.Y, false);
            if (GUILayout.Button("Z-"))
                this._moveNode(MoveDirs.Z, false);
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            var cPos = this._selectedNode.position;
            var posText = string.Format("{0}, {1}, {2}", _formatNumberForOutput(cPos.x), _formatNumberForOutput(cPos.y), _formatNumberForOutput(cPos.z));
            GUILayout.Label("Position (" + posText + ")", expandWidth);
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            this._targetPos = GUILayout.TextField(this._targetPos, GUILayout.Width(180));

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy Pos", expandWidth))
                this._targetPos = posText;
            if (GUILayout.Button("Set Pos", expandWidth))
                this._setToPos();
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Use Default", expandWidth))
                this._resetCurrNode();
            if (GUILayout.Button("Set Default", expandWidth))
                this._updateResetPosCurrNode();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            //=====================================================================================
            GUILayout.BeginVertical("box");

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            GUILayout.Label("Size: " + this._selectedNode.size, expandWidth);
            if (GUILayout.Button("-1", expandWidth) && (this._selectedNode.size > 0))
                this._selectedNode.size -= 1;
            if (GUILayout.Button("+1", expandWidth) && (this._selectedNode.size < int.MaxValue - 1))
                this._selectedNode.size += 1;
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            var or = this._selectedNode.orientation;
            var orientationString = _formatNumberForOutput(or.x) + ", " + _formatNumberForOutput(or.y) + ", " + _formatNumberForOutput(or.z);
            GUILayout.Label("Orient: " + orientationString, expandWidth);

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+X", expandWidth))
                this._selectedNode.orientation = new Vector3(1f, 0f, 0f);
            if (GUILayout.Button("+Y", expandWidth))
                this._selectedNode.orientation = new Vector3(0f, 1f, 0f);
            if (GUILayout.Button("+Z", expandWidth))
                this._selectedNode.orientation = new Vector3(0f, 0f, 1f);
            this._nodeOrientationCust = GUILayout.TextField(this._nodeOrientationCust, expandWidth);
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("-X", expandWidth))
                this._selectedNode.orientation = new Vector3(-1f, 0f, 0f);
            if (GUILayout.Button("-Y", expandWidth))
                this._selectedNode.orientation = new Vector3(0f, -1f, 0f);
            if (GUILayout.Button("-Z", expandWidth))
                this._selectedNode.orientation = new Vector3(0f, 0f, -1f);
            if (GUILayout.Button("Custom", expandWidth))
                this._orientNodeToCust();
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            this._showOrientationPointer = GUILayout.Toggle(this._showOrientationPointer, "Show Orientation Pointer", "Button", expandWidth);

            GUILayout.EndVertical();

            //-------------------------------------------------------------------------------------
            if (GUILayout.Button("Delete node", expandWidth))
                this._deleteCurrNode();

            GUI.DragWindow();
        }


        Vector2 scrollPos = new Vector2(0f, 0f);


        protected void NodeListGui(int windowID)
        {
            var expandWidth = GUILayout.ExpandWidth(true);

            //=====================================================================================
            if (this._selectedPart == null)
            {
                GUILayout.Label("Right-click a part to select it.", expandWidth);
                /*if (GUILayout.Button("Save Settings"))
                    if (this._settings.Save("GameData/Felbourn/NodeHelper/settings.cfg", "NodeHelper"))
                        Debug.Log("[NH] saving GameData/Felbourn/NodeHelper/settings.cfg");
                    else
                        Debug.LogWarning("[NH] unable to save GameData/Felbourn/NodeHelper/settings.cfg");*/

                GUI.DragWindow();
                return;
            }

            //=====================================================================================
            GUILayout.BeginVertical("box");

            if (this._nodeMapping.Keys.Count > 20)
                scrollPos = GUILayout.BeginScrollView(scrollPos,
                     GUILayout.Width(200), GUILayout.Height(500));


            foreach (var node in this._nodeMapping.Keys)
            {
                var isSel = this._selectedNode != null && this._selectedNode == node;
                GUILayout.BeginHorizontal();
                if (GUILayout.Toggle(isSel, this._getNodeName(node), "Button", expandWidth))
                    this._selectedNode = node;
                GUILayout.EndHorizontal();
            }
            if (this._nodeMapping.Keys.Count > 20)
                GUILayout.EndScrollView();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        protected void WindowGui(int windowID)
        {
            var expandWidth = GUILayout.ExpandWidth(true);

            //=====================================================================================
            GUILayout.BeginVertical("box");
            GUILayout.Label("Attachment Rules: " + this._getSelPartAttRulesString());
            var tempArr = new bool[5];
            Array.Copy(this._selectedPartRules, tempArr, 5);

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            tempArr[0] = GUILayout.Toggle(tempArr[0], "stack", "Button", expandWidth);
            tempArr[1] = GUILayout.Toggle(tempArr[1], "srfAttach", "Button", expandWidth);
            tempArr[2] = GUILayout.Toggle(tempArr[2], "allowStack", "Button", expandWidth);
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            tempArr[3] = GUILayout.Toggle(tempArr[3], "allowSrfAttach", "Button", expandWidth);
            tempArr[4] = GUILayout.Toggle(tempArr[4], "allowCollision", "Button", expandWidth);
            this._processAttachRules(tempArr);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            //=====================================================================================
            GUILayout.BeginVertical("box");
            if (!this._showCreateMenu)
            {
                if (GUILayout.Button("Show Node Creation"))
                    this._showCreateMenu = true;
            }
            else
            {
                if (GUILayout.Button("Hide Node Creation"))
                    this._showCreateMenu = false;

                GUILayout.Label("Note: New attachment nodes are not automatically saved with the craft file.", GUILayout.ExpandWidth(false));

                //-------------------------------------------------------------------------------------
                GUILayout.BeginHorizontal();
                GUILayout.Label("Add a node", expandWidth);
                if (GUILayout.Button("Create"))
                    this._createNewNode();
                GUILayout.EndHorizontal();

                //-------------------------------------------------------------------------------------
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name:", expandWidth);
                this._newNodeName = GUILayout.TextField(this._newNodeName, GUILayout.Width(120));
                GUILayout.EndHorizontal();

                //-------------------------------------------------------------------------------------
                GUILayout.BeginHorizontal();
                GUILayout.Label("Pos:", expandWidth);
                this._newNodePos = GUILayout.TextField(this._newNodePos, GUILayout.Width(120));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            //=====================================================================================
            GUILayout.BeginVertical("box");

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            GUILayout.Label("Normal planes", expandWidth);
            this._showPlanes[0] = GUILayout.Toggle(this._showPlanes[0], "X", "Button");
            this._showPlanes[1] = GUILayout.Toggle(this._showPlanes[1], "Y", "Button");
            this._showPlanes[2] = GUILayout.Toggle(this._showPlanes[2], "Z", "Button");
            GUILayout.EndHorizontal();

            //-------------------------------------------------------------------------------------
            GUILayout.BeginHorizontal();
            GUILayout.Label("Radius");
            this._planeRadiusString = GUILayout.TextField(this._planeRadiusString, GUILayout.Width(90));
            if (GUILayout.Button("Set"))
                this._parsePlaneRadius();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            //-------------------------------------------------------------------------------------
            if (GUILayout.Button("Write node data") && this._selectedPart != null && !this._printingActive)
            {
                this._printingActive = true;
                this._printNodeConfigForPart(!PrintAdvancedConfig);
            }

            GUI.DragWindow();
        }

        private void _cleanSelectedPartSetup()
        {
            if (this._selectedPart == null)
                return;
            this._selectedPart.SetHighlightDefault();
        }

        private void _clearMapping(bool deselect = true)
        {
            if (!this._initialized)
                return;

            this._orientationPointer.SetActive(false);
            foreach (var kv in this._nodeMapping)
                Destroy(kv.Value);

            this._nodeMapping.Clear();
            this._nodeNameMapping.Clear();

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

        private List<Utils.Tuple<string, string>> _constructNodeValues()
        {
            var nameList = new List<AttachNode>(this._selectedPart.attachNodes.Count + 1);
            nameList.AddRange(this._selectedPart.attachNodes);
            var retList = _uniquifyNames(nameList).Select(attachNode => _nodeToString(attachNode.Key, attachNode.Value)).ToList();
            if (this._selectedPart.srfAttachNode != null)
                retList.Add(_nodeToString(this._selectedPart.srfAttachNode, string.Empty, false));
            return retList;
        }

        private void _createNewNode()
        {
            try
            {
                if (string.IsNullOrEmpty(this._newNodeName))
                {
                    OSD.PostMessageUpperCenter("[NH] name for new node empty");
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
                this._planes[i] = Utils.UI.CreatePrimitive(type: PrimitiveType.Cube, color: this._planeColor, scale: new Vector3(1f, 1f, 1f), isActive: false, hasCollider: false, hasRigidbody: false, name: "Helper Plane", shader: TransShader);
        }

        private void _deleteCurrNode()
        {
            if (this._selectedNode == null)
                return;

            this._selectedPart.attachNodes.Remove(this._selectedNode);
            this._clearMapping(false);
            this._selectedNode = null;
            OSD.PostMessageUpperCenter("[NH] node deleted");
        }

        private string _findUniqueId(string newNodeName)
        {
            if (this._nodeNameMapping.Keys.All(k => k.id != newNodeName))
                return newNodeName;

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
            while (inNr % 1 > 0)
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
                trimmedString = trimmedString + "0";
            return trimmedString;
        }

        private string _getNodeName(AttachNode node)
        {
            if (this._nodeNameMapping.ContainsKey(node))
                return this._nodeNameMapping[node];

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
                    sb.Append(",");
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

        private static Utils.Tuple<string, string> _nodeToString(AttachNode node, string id, bool stack = true)
        {
            const string delim = ", ";

            string retKey;
            if (stack)
                retKey = "node_stack_" + id;
            else
                retKey = "node_attach";

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
            return new Utils.Tuple<string, string>(retKey, sb.ToString());
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
                return;
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
                pr = Mathf.Abs(pr);
            this._planeRadius = pr;
            this._planeRadiusString = _formatNumberForOutput(pr);
        }

        private void _parseStepWidth()
        {
            var psw = 0f;
            float sw;
            if (float.TryParse(this._stepWidthString, out sw))
                psw = Mathf.Abs(sw);
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
                        newConf.AddValue(nodeValue.Item1, nodeValue.Item2);
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
                return;

            var arr = this._selectedPartRules;
            var pr = this._selectedPart.attachRules;
            if (arr[0] != tempArr[0])
                pr.stack = !pr.stack;
            if (arr[1] != tempArr[1])
                pr.srfAttach = !pr.srfAttach;
            if (arr[2] != tempArr[2])
                pr.allowStack = !pr.allowStack;
            if (arr[3] != tempArr[3])
                pr.allowSrfAttach = !pr.allowSrfAttach;
            if (arr[4] != tempArr[4])
                pr.allowCollision = !pr.allowCollision;
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
                pT.localScale = new Vector3(diameter, 0.01f, diameter);
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
                this._orientationPointer.SetActive(true);
            else
                this._orientationPointer.SetActive(false);
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
                return;

            this._selectedPart.SetHighlightColor(Color.blue);
            this._selectedPart.SetHighlight(true, false);
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
                    n = n + "_" + (cnt + 1);
                nameDic.Add(attachNode, n);
            }
            return nameDic;
        }

        private void _updateAttachRules()
        {
            if (this._selectedPart == null || this._selectedPart.attachRules == null)
                return;

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
                mr.material.color = color;
        }

        private void _updateMapping()
        {
            foreach (var attachNode in this._selectedPart.attachNodes.Where(an => an != null))
            {
                if (this._nodeMapping.ContainsKey(attachNode))
                    continue;
                if (this._selectedPart.srfAttachNode != null && attachNode == this._selectedPart.srfAttachNode)
                    continue;

                var scale = GetGoScaleForNode(attachNode);
                var go = Utils.UI.CreatePrimitive(PrimitiveType.Sphere, this._nodeColor, scale, true, false, false, name:  "Helper Node", shader: TransShader);
                go.GetComponent<MeshRenderer>().material = this._nodeMaterial;
                go.transform.SetParent(this._selectedPart.transform); //xxx
                this._nodeMapping.Add(attachNode, go);
                this._nodeNameMapping.Add(attachNode, attachNode.id);
                if (!this._nodePosBackup.ContainsKey(attachNode))
                    this._nodePosBackup.Add(attachNode, attachNode.position);
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
