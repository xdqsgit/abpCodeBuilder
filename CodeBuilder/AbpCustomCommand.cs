using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeBuilder.Models.TemplateModels;
using CodeBuilder.Templates;
using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RazorEngine;
using RazorEngine.Configuration;
using RazorEngine.Templating;
using Engine = RazorEngine.Engine;
using ProjectItem = EnvDTE.ProjectItem;
using Task = System.Threading.Tasks.Task;

namespace CodeBuilder
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class AbpCustomCommand
    {
        public static DTE _dte;

        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("a9dcde5b-5ac0-4b3e-841c-fe85a46c7f4a");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private static AsyncPackage asyncPackage;

        ///// <summary>
        ///// Initializes a new instance of the <see cref="AbpCustomCommand"/> class.
        ///// Adds our command handlers for menu (commands must exist in the command table file)
        ///// </summary>
        ///// <param name="package">Owner package, not null.</param>
        ///// <param name="commandService">Command service to add command to, not null.</param>
        //private AbpCustomCommand(AsyncPackage package, OleMenuCommandService commandService)
        //{
        //    InitRazorEngine();
        //    this.package = package ?? throw new ArgumentNullException(nameof(package));
        //    commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

        //    var menuCommandID = new CommandID(CommandSet, CommandId);
        //    var menuItem = new MenuCommand(this.Execute, menuCommandID);
        //    menuItem.Supported = false;//默认不显示此菜单
        //    commandService.AddCommand(menuItem);
        //}

        private static void InitRazorEngine()
        {
            var config = new TemplateServiceConfiguration
            {
                TemplateManager = new EmbeddedResourceTemplateManager(typeof(Template))
            };
            Engine.Razor = RazorEngineService.Create(config);
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in AbpCustomCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            _dte = await package.GetServiceAsync(typeof(DTE)) as DTE;
            asyncPackage = package;
            OleMenuCommandService commandService = await package.GetServiceAsync((typeof(IMenuCommandService))) as OleMenuCommandService;
            InitRazorEngine();
            var cmdId = new CommandID(CommandSet, CommandId);
            var cmd = new OleMenuCommand(Execute, cmdId)
            {
                // This will defer visibility control to the VisibilityConstraints section in the .vsct file
                Supported = false
            };
            commandService.AddCommand(cmd);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private static void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string message = "";

            if (_dte.SelectedItems.Count > 0)
            {
                SelectedItem selectedItem = _dte.SelectedItems.Item(1);
                ProjectItem selectProjectItem = selectedItem.ProjectItem;

                if (selectProjectItem != null)
                {
                    //前端工程源码目录
                    string frontBaseUrl = "";

                    #region 获取出基础信息
                    //获取当前点击的类所在的项目
                    Project topProject = selectProjectItem.ContainingProject;
                    //当前类在当前项目中的目录结构
                    string dirPath = GetSelectFileDirPath(topProject, selectProjectItem);
                    
                    //当前类命名空间
                    string namespaceStr = selectProjectItem.FileCodeModel.CodeElements.OfType<CodeNamespace>().First().FullName;
                    //当前项目根命名空间
                    string applicationStr = "";
                    if (!string.IsNullOrEmpty(namespaceStr))
                    {
                        applicationStr = namespaceStr.Substring(0, namespaceStr.IndexOf("."));
                    }
                    //当前类
                    CodeClass codeClass = GetClass(selectProjectItem.FileCodeModel.CodeElements);
                    //当前项目类名
                    string className = codeClass.Name;
                    //当前类中文名 [Display(Name = "供应商")]
                    string classCnName = "";
                    //当前类说明 [Description("品牌信息")]
                    string classDescription = "";
                    //获取类的中文名称和说明
                    foreach (CodeAttribute classAttribute in codeClass.Attributes)
                    {
                        switch (classAttribute.Name)
                        {
                            case "Display":
                                if (!string.IsNullOrEmpty(classAttribute.Value))
                                {
                                    string displayStr = classAttribute.Value.Trim();
                                    foreach (var displayValueStr in displayStr.Split(','))
                                    {
                                        if (!string.IsNullOrEmpty(displayValueStr))
                                        {
                                            if (displayValueStr.Split('=')[0].Trim() == "Name")
                                            {
                                                classCnName = displayValueStr.Split('=')[1].Trim().Replace("\"", "");
                                            }
                                        }
                                    }
                                }
                                break;
                            case "Description":
                                classDescription = classAttribute.Value;
                                break;
                        }
                    }

                    //获取当前解决方案里面的项目列表
                    List<ProjectItem> solutionProjectItems = GetSolutionProjects(_dte.Solution);
                    #endregion

                    #region 流程简介
                    //1.同级目录添加 Authorization 文件夹
                    //2.往新增的 Authorization 文件夹中添加 xxxPermissions.cs 文件 
                    //3.往新增的 Authorization 文件夹中添加 xxxAuthorizationProvider.cs 文件
                    //4.往当前项目根目录下文件夹 Authorization 里面的AppAuthorizationProvider.cs类中的SetPermissions方法最后加入 SetxxxPermissions(pages); 
                    //5.往xxxxx.Application项目中增加当前所选文件所在的文件夹
                    //6.往第五步新增的文件夹中增加Dto目录
                    //7.往第六步新增的Dto中增加CreateOrUpdatexxxInput.cs  xxxEditDto.cs  xxxListDto.cs  GetxxxForEditOutput.cs  GetxxxsInput.cs这五个文件
                    //8.编辑CustomDtoMapper.cs,添加映射
                    //9.往第五步新增的文件夹中增加 xxxAppService.cs和IxxxAppService.cs 类
                    //10.编辑DbContext
                    //11.新增前端文件
                    #endregion

                    #region 流程实现
                    ////1.同级目录添加 Authorization 文件夹
                    //var authorizationFolder = selectProjectItem.ProjectItems.AddFolder("Authorization");//向同级目录插入文件夹

                    ////2.往新增的 Authorization 文件夹中添加 xxxPermissions.cs 文件 
                    //CreatePermissionFile(applicationStr, className, authorizationFolder);

                    ////3.往新增的 Authorization 文件夹中添加 xxxAuthorizationProvider.cs 文件
                    //CreateAppAuthorizationProviderFile(applicationStr, className, classCnName, authorizationFolder);

                    ////4.往当前项目根目录下文件夹 Authorization 里面的 AppAuthorizationProvider.cs类中的 SetPermissions 方法最后加入 SetxxxPermissions(pages); 
                    //SetPermission(topProject, className);

                    ////5.往xxxxx.Application项目中增加当前所选文件所在的文件夹
                    //ProjectItem applicationProjectItem = solutionProjectItems.Find(t => t.Name == applicationStr + ".Application");
                    //var applicationNewFolder = applicationProjectItem.SubProject.ProjectItems.Item(dirPath);
                    //if (applicationNewFolder == null)
                    //{
                    //    applicationNewFolder = applicationProjectItem.SubProject.ProjectItems.AddFolder(dirPath);
                    //}

                    ////6.往第五步新增的文件夹中增加Dto目录
                    //var applicationDtoFolder = applicationNewFolder.ProjectItems.Item("Dto");
                    //if (applicationDtoFolder == null)
                    //{
                    //    applicationDtoFolder = applicationNewFolder.ProjectItems.AddFolder("Dto");
                    //}

                    ////7.往第六步新增的Dto中增加CreateOrUpdatexxxInput.cs  xxxEditDto.cs  xxxListDto.cs  GetxxxForEditOutput.cs  GetxxxsInput.cs这五个文件
                    DtoFileModel dtoModel = GetDtoModel(applicationStr, className, classCnName, classDescription, dirPath, codeClass);
                    //CreateDtoFile(dtoModel, className, applicationDtoFolder);

                    ////8.编辑CustomDtoMapper.cs,添加映射
                    //SetMapper(applicationProjectItem.SubProject, className, classCnName);

                    ////9.往第五步新增的文件夹中增加 xxxAppService.cs和IxxxAppService.cs 类
                    //CreateServiceFile(applicationStr, className, classCnName, applicationNewFolder, dirPath, codeClass);

                    ////10.编辑DbContext
                    //ProjectItem entityFrameworkProjectItem = solutionProjectItems.Find(t => t.Name == applicationStr + ".EntityFrameworkCore");
                    //SetDbSetToDbContext(entityFrameworkProjectItem.SubProject, namespaceStr, className);

                    //11.生成前端
                    frontBaseUrl = topProject.FullName.Substring(0, topProject.FullName.IndexOf("src") - 1);
                    frontBaseUrl = frontBaseUrl.Substring(0, frontBaseUrl.LastIndexOf("\\")) + "\\angular\\src\\";

                    //11.1 往app\\admin文件夹下面加xxx文件夹
                    string componetBasePath = frontBaseUrl + "app\\admin\\" + dirPath.ToLower();
                    if (!Directory.Exists(componetBasePath))
                    {
                        Directory.CreateDirectory(componetBasePath);
                    }

                    //11.2 往新增的文件夹加xxx.component.html   xxx.component.ts   create-or-edit-xxx-modal.component.html  create-or-edit-xxx-modal.component.ts这4个文件
                    CreateFrontFile(dtoModel, className, componetBasePath);
                    //11.3 修改app\\admin\\admin.module.ts文件，  import新增的组件   注入组件
                    EditModule(frontBaseUrl, className, dirPath);
                    //11.4 修改app\\admin\\admin-routing.module.ts文件   添加路由
                    EditRouter(frontBaseUrl, className, dirPath);
                    //11.5 修改 app\\shared\\layout\\nav\\app-navigation.service.ts文件   添加菜单
                    AddMenu(frontBaseUrl, classCnName, className);
                    //11.6 修改 shared\\service-proxies\\service-proxy.module.ts文件  提供服务
                    AddProxy(frontBaseUrl, className);

                    //如果需要新增一级目录的话，需要修改app\\\app-routing.module.ts文件

                    message = "生成成功！";
                    #endregion
                }
            }
            string title = "abp代码生成器";

            // Show a message box to prove we were here
            VsShellUtilities.ShowMessageBox(
                asyncPackage,
                message,
                title,
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }


        #region 代码生成

        /// <summary>
        /// 获取DtoModel
        /// </summary>
        /// <param name="applicationStr"></param>
        /// <param name="name"></param>
        /// <param name="dirName"></param>
        /// <param name="codeClass"></param>
        /// <returns></returns>
        private static DtoFileModel GetDtoModel(string applicationStr, string name, string cnName, string description, string dirName, CodeClass codeClass)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var model = new DtoFileModel() { Namespace = applicationStr, Name = name, CnName = cnName, Description = description, DirName = dirName.Replace("\\", ".") };
            List<ClassProperty> classProperties = new List<ClassProperty>();
            List<ClassAttribute> classAttributes = null;

            var codeMembers = codeClass.Members;
            foreach (CodeElement codeMember in codeMembers)
            {
                if (codeMember.Kind == vsCMElement.vsCMElementProperty)
                {
                    ClassProperty classProperty = new ClassProperty();
                    CodeProperty property = codeMember as CodeProperty;
                    classProperty.Name = property.Name;
                    //获取属性类型
                    var propertyType = property.Type;
                    switch (propertyType.TypeKind)
                    {
                        case vsCMTypeRef.vsCMTypeRefString:
                            classProperty.PropertyType = "string";
                            break;
                        case vsCMTypeRef.vsCMTypeRefInt:
                            classProperty.PropertyType = "int";
                            break;
                        case vsCMTypeRef.vsCMTypeRefBool:
                            classProperty.PropertyType = "bool";
                            break;
                        case vsCMTypeRef.vsCMTypeRefDecimal:
                            classProperty.PropertyType = "decimal";
                            break;
                        case vsCMTypeRef.vsCMTypeRefDouble:
                            classProperty.PropertyType = "double";
                            break;
                        case vsCMTypeRef.vsCMTypeRefFloat:
                            classProperty.PropertyType = "float";
                            break;
                    }

                    string propertyCnName = "";//属性中文名称

                    classAttributes = new List<ClassAttribute>();
                    //获取属性特性
                    foreach (CodeAttribute codeAttribute in property.Attributes)
                    {
                        ClassAttribute classAttribute = new ClassAttribute();
                        if (codeAttribute.Name == "Required")
                        {
                            classAttribute.NameValue = "[Required]";

                            classAttribute.Name = "Required";
                            classAttribute.Value = "true";
                        }
                        else
                        {
                            classAttribute.NameValue = "[" + codeAttribute.Name + "(" + codeAttribute.Value + ")]";
                            classAttribute.Name = codeAttribute.Name;
                            classAttribute.Value = codeAttribute.Value;

                            if (codeAttribute.Name == "Display")
                            {
                                propertyCnName = codeAttribute.Value.Replace("Name = ", "").Replace("\"", "");
                            }
                        }
                        classAttributes.Add(classAttribute);
                    }

                    classProperty.CnName = string.IsNullOrEmpty(propertyCnName) ? property.Name : propertyCnName;
                    classProperty.ClassAttributes = classAttributes;

                    classProperties.Add(classProperty);
                }
            }

            model.ClassPropertys = classProperties;

            return model;
        }

        #region 生成后端
        /// <summary>
        /// 创建Permissions权限常量类
        /// </summary>
        /// <param name="applicationStr">根命名空间</param>
        /// <param name="name">类名</param>
        /// <param name="authorizationFolder">父文件夹</param>
        private void CreatePermissionFile(string applicationStr, string name, ProjectItem authorizationFolder)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var model = new PermissionsFileModel() { Namespace = applicationStr, Name = name };
            string content = Engine.Razor.RunCompile("PermissionsTemplate", typeof(PermissionsFileModel), model);
            string fileName = $"{name}Permissions.cs";
            AddFileToProjectItem(authorizationFolder, content, fileName);
        }

        /// <summary>
        /// 创建AppAuthorizationProvider权限配置类
        /// </summary>
        /// <param name="applicationStr">根命名空间</param>
        /// <param name="name">类名</param>
        /// <param name="authorizationFolder">父文件夹</param>
        private void CreateAppAuthorizationProviderFile(string applicationStr, string name, string cnName, ProjectItem authorizationFolder)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var model = new AppAuthorizationProviderFileModel() { Namespace = applicationStr, Name = name, CnName = cnName };
            string content = Engine.Razor.RunCompile("AppAuthorizationProviderTemplate", typeof(AppAuthorizationProviderFileModel), model);
            string fileName = name + "AuthorizationProvider.cs";
            AddFileToProjectItem(authorizationFolder, content, fileName);
        }

        /// <summary>
        /// 添加权限
        /// </summary>
        /// <param name="topProject"></param>
        /// <param name="className"></param>
        private void SetPermission(Project topProject, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ProjectItem AppAuthorizationProviderProjectItem = _dte.Solution.FindProjectItem(topProject.FileName.Substring(0, topProject.FileName.LastIndexOf("\\")) + "\\Authorization\\AppAuthorizationProvider.cs");
            if (AppAuthorizationProviderProjectItem != null)
            {
                CodeClass codeClass = GetClass(AppAuthorizationProviderProjectItem.FileCodeModel.CodeElements);
                var codeChilds = codeClass.Members;
                foreach (CodeElement codeChild in codeChilds)
                {
                    if (codeChild.Kind == vsCMElement.vsCMElementFunction && codeChild.Name == "SetPermissions")
                    {
                        var insertCode = codeChild.GetEndPoint(vsCMPart.vsCMPartBody).CreateEditPoint();
                        insertCode.Insert("            Set" + className + "Permissions(pages);\r\n");
                        insertCode.Insert("\r\n");
                    }
                }
                AppAuthorizationProviderProjectItem.Save();
            }
        }

        /// <summary>
        /// 创建Dto类
        /// </summary>
        /// <param name="model"></param>
        /// <param name="name"></param>
        /// <param name="dtoFolder"></param>
        private void CreateDtoFile(DtoFileModel model, string name, ProjectItem dtoFolder)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string content_Edit = Engine.Razor.RunCompile("EditDtoTemplate", typeof(DtoFileModel), model);
            string fileName_Edit = $"{name}EditDto.cs";
            AddFileToProjectItem(dtoFolder, content_Edit, fileName_Edit);

            string content_List = Engine.Razor.RunCompile("ListDtoTemplate", typeof(DtoFileModel), model);
            string fileName_List = $"{name}ListDto.cs";
            AddFileToProjectItem(dtoFolder, content_List, fileName_List);

            string content_CreateAndUpdate = Engine.Razor.RunCompile("CreateOrUpdateInputDtoTemplate", typeof(DtoFileModel), model);
            string fileName_CreateAndUpdate = $"CreateOrUpdate{name}Input.cs";
            AddFileToProjectItem(dtoFolder, content_CreateAndUpdate, fileName_CreateAndUpdate);

            string content_GetForUpdate = Engine.Razor.RunCompile("GetForEditOutputDtoTemplate", typeof(DtoFileModel), model);
            string fileName_GetForUpdate = $"Get{name}ForEditOutput.cs";
            AddFileToProjectItem(dtoFolder, content_GetForUpdate, fileName_GetForUpdate);

            string content_GetsInput = Engine.Razor.RunCompile("GetsInputTemplate", typeof(DtoFileModel), model);
            string fileName_GetsInput = $"Get{name}sInput.cs";
            AddFileToProjectItem(dtoFolder, content_GetsInput, fileName_GetsInput);
        }

        /// <summary>
        /// 创建Service类
        /// </summary>
        /// <param name="applicationStr">根命名空间</param>
        /// <param name="name">类名</param>
        /// <param name="dtoFolder">父文件夹</param>
        /// <param name="dirName">类所在文件夹目录</param>
        private void CreateServiceFile(string applicationStr, string name, string cnName, ProjectItem dtoFolder, string dirName, CodeClass codeClass)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var model = new ServiceFileModel() { Namespace = applicationStr, Name = name, CnName = cnName, DirName = dirName.Replace("\\", ".") };

            string content_IService = Engine.Razor.RunCompile("IServiceTemplate", typeof(ServiceFileModel), model);
            string fileName_IService = $"I{name}AppService.cs";
            AddFileToProjectItem(dtoFolder, content_IService, fileName_IService);

            string content_Service = Engine.Razor.RunCompile("ServiceTemplate", typeof(ServiceFileModel), model);
            string fileName_Service = $"{name}AppService.cs";
            AddFileToProjectItem(dtoFolder, content_Service, fileName_Service);
        }

        /// <summary>
        /// 添加DbSet到DbContext
        /// </summary>
        /// <param name="topProject"></param>
        /// <param name="className"></param>
        private void SetDbSetToDbContext(Project entityFrameworkProject, string namespaceStr, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ProjectItem customDbContextProviderProjectItem = _dte.Solution.FindProjectItem(entityFrameworkProject.FileName.Substring(0, entityFrameworkProject.FileName.LastIndexOf("\\")) + "\\EntityFrameworkCore\\AbpFrameDbContext.Custom.cs");
            if (customDbContextProviderProjectItem != null)
            {
                CodeClass codeClass = GetClass(customDbContextProviderProjectItem.FileCodeModel.CodeElements);
                var codeChilds = codeClass.Collection;
                foreach (CodeElement codeChild in codeChilds)
                {
                    var insertCode = codeChild.GetEndPoint(vsCMPart.vsCMPartBody).CreateEditPoint();
                    insertCode.Insert("        public DbSet<"+ namespaceStr + "." + className + "> "+ className + "s { get; set; }\r\n");
                }

                customDbContextProviderProjectItem.Save();
            }
        }

        /// <summary>
        /// 编辑CustomDtoMapper.cs,添加映射
        /// </summary>
        /// <param name="applicationProject"></param>
        /// <param name="className"></param>
        /// <param name="classCnName"></param>
        private void SetMapper(Project applicationProject, string className, string classCnName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ProjectItem customDtoMapperProjectItem = _dte.Solution.FindProjectItem(applicationProject.FileName.Substring(0, applicationProject.FileName.LastIndexOf("\\")) + "\\CustomDtoMapper.cs");
            if (customDtoMapperProjectItem != null)
            {
                CodeClass codeClass = GetClass(customDtoMapperProjectItem.FileCodeModel.CodeElements);
                var codeChilds = codeClass.Members;
                foreach (CodeElement codeChild in codeChilds)
                {
                    if (codeChild.Kind == vsCMElement.vsCMElementFunction && codeChild.Name == "CreateMappings")
                    {
                        var insertCode = codeChild.GetEndPoint(vsCMPart.vsCMPartBody).CreateEditPoint();
                        insertCode.Insert("            // " + classCnName + "\r\n");
                        insertCode.Insert("            configuration.CreateMap<" + className + ", " + className + "EditDto>();\r\n");
                        insertCode.Insert("            configuration.CreateMap<" + className + ", " + className + "ListDto>();\r\n");
                        insertCode.Insert("            configuration.CreateMap<" + className + "EditDto, " + className + ">();\r\n");
                        insertCode.Insert("            configuration.CreateMap<" + className + "ListDto, " + className + ">();\r\n");
                        insertCode.Insert("\r\n");
                    }
                }

                customDtoMapperProjectItem.Save();
            }
        }

        #endregion

        #region 生成前端
        /// <summary>
        /// 创建前端文件
        /// </summary>
        /// <param name="model"></param>
        /// <param name="name"></param>
        /// <param name="frontPath"></param>
        private static void CreateFrontFile(DtoFileModel model, string name, string frontPath)
        {
            string content_List_Html = Engine.Razor.RunCompile("Front_List_HtmlTemplate", typeof(DtoFileModel), model);
            string fileName_List_Html = $"{name.ToLower()}.component.html";
            AddFileToDirectory(frontPath, content_List_Html, fileName_List_Html);

            string content_List_Ts = Engine.Razor.RunCompile("Front_List_TsTemplate", typeof(DtoFileModel), model);
            string fileName_List_Ts = $"{name.ToLower()}.component.ts";
            AddFileToDirectory(frontPath, content_List_Ts, fileName_List_Ts);

            string content_Edit_Html = Engine.Razor.RunCompile("Front_Edit_HtmlTemplate", typeof(DtoFileModel), model);
            string fileName_Edit_Html = $"create-or-edit-{name.ToLower()}-modal.component.html";
            AddFileToDirectory(frontPath, content_Edit_Html, fileName_Edit_Html);

            string content_Edit_Ts = Engine.Razor.RunCompile("Front_Edit_TsTemplate", typeof(DtoFileModel), model);
            string fileName_Edit_Ts = $"create-or-edit-{name.ToLower()}-modal.component.ts";
            AddFileToDirectory(frontPath, content_Edit_Ts, fileName_Edit_Ts);
        }

        /// <summary>
        /// 导入并注入模块
        /// </summary>
        /// <param name="frontPath"></param>
        /// <param name="name"></param>
        private static void EditModule(string frontPath, string name, string dirPath)
        {
            string importCode = "import { " + name + "Component } from './" + dirPath.Replace("\\", ".").ToLower() + "/" + name.ToLower() + ".component';\r\n";
            importCode += "import { CreateOrEdit" + name + "ModalComponent } from './" + dirPath.Replace("\\", ".").ToLower() + "/create-or-edit-" + name.ToLower() + "-modal.component';\r\n";
            importCode += "// {#insert import code#}\r\n";

            string declarationsCode = name + "Component,\r\n";
            declarationsCode += "    CreateOrEdit" + name + "ModalComponent,\r\n";
            declarationsCode += "    // {#insert declarations code#}\r\n";

            string entryComponentsCode = name + "Component,\r\n";
            entryComponentsCode += "    CreateOrEdit" + name + "ModalComponent,\r\n";
            entryComponentsCode += "    // {#insert declarations code#}\r\n";

            string moduleFilePath = frontPath + "app\\admin\\admin.module.ts";
            string moduleContent = File.ReadAllText(moduleFilePath);
            moduleContent = moduleContent.Replace("// {#insert import code#}", importCode);
            moduleContent = moduleContent.Replace("// {#insert declarations code#}", declarationsCode);
            moduleContent = moduleContent.Replace("// {#insert entryComponents code#}", entryComponentsCode);

            AddFileToDirectory(moduleFilePath, moduleContent);
        }

        /// <summary>
        /// 添加路由，目前都是加到admin子路由中
        /// </summary>
        /// <param name="frontPath"></param>
        /// <param name="name"></param>
        private static void EditRouter(string frontPath, string name, string dirPath)
        {
            string importCode = "import { " + name + "Component } from './" + dirPath.Replace("\\", ".").ToLower() + "/" + name.ToLower() + ".component';\r\n";
            importCode += "// {#insert import code#}\r\n";

            string routesCode = "{ path: '"+ name.ToLower() +"', component: "+ name +"Component, data: { permission: 'Pages."+ name +"' } },\r\n";
            routesCode += "    // {#insert routes code#}\r\n";

            string routerFilePath = frontPath + "app\\admin\\admin-routing.module.ts";
            string routerContent = File.ReadAllText(routerFilePath);
            routerContent = routerContent.Replace("// {#insert import code#}", importCode);
            routerContent = routerContent.Replace("// {#insert routes code#}", routesCode);

            AddFileToDirectory(routerFilePath, routerContent);
        }

        /// <summary>
        /// 添加菜单
        /// </summary>
        /// <param name="frontPath"></param>
        /// <param name="cnName"></param>
        /// <param name="name"></param>
        private static void AddMenu(string frontPath, string cnName, string name)
        {
            string importCode = "new AppMenuItem('"+ cnName + "', 'Pages."+ name + "', 'anticon anticon-setting', '/app/admin/"+ name.ToLower() + "'),\r\n";
            importCode += "                // {#insert menu code#}\r\n";

            string menuFilePath = frontPath + "app\\shared\\layout\\nav\\app-navigation.service.ts";
            string menuContent = File.ReadAllText(menuFilePath);
            menuContent = menuContent.Replace("// {#insert menu code#}", importCode);

            AddFileToDirectory(menuFilePath, menuContent);
        }

        /// <summary>
        /// 注入服务
        /// </summary>
        /// <param name="frontPath"></param>
        /// <param name="name"></param>
        private static void AddProxy(string frontPath, string name)
        {
            string routesCode = "ApiServiceProxies."+ name + "ServiceProxy,\r\n";
            routesCode += "        // {#insert routes code#}\r\n";

            string proxyFilePath = frontPath + "shared\\service-proxies\\service-proxy.module.ts";
            string proxyContent = File.ReadAllText(proxyFilePath);
            proxyContent = proxyContent.Replace("// {#insert proxy code#}", routesCode);

            AddFileToDirectory(proxyFilePath, proxyContent);
        }
        #endregion
        #endregion

        #region 辅助方法
        /// <summary>
        /// 获取所有项目
        /// </summary>
        /// <param name="projectItems"></param>
        /// <returns></returns>
        private static IEnumerable<ProjectItem> GetProjects(ProjectItems projectItems)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ProjectItem item in projectItems)
            {
                yield return item;

                if (item.SubProject != null)
                {
                    foreach (ProjectItem childItem in GetProjects(item.SubProject.ProjectItems))
                        if (childItem.Kind == EnvDTE.Constants.vsProjectItemKindSolutionItems)
                            yield return childItem;
                }
                else
                {
                    foreach (ProjectItem childItem in GetProjects(item.ProjectItems))
                        if (childItem.Kind == EnvDTE.Constants.vsProjectItemKindSolutionItems)
                            yield return childItem;
                }
            }
        }

        /// <summary>
        /// 获取解决方案里面所有项目
        /// </summary>
        /// <param name="solution"></param>
        /// <returns></returns>
        private static List<ProjectItem> GetSolutionProjects(Solution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<ProjectItem> projectItemList = new List<ProjectItem>();
            var projects = solution.Projects.OfType<Project>();
            foreach (var project in projects)
            {
                var projectitems = GetProjects(project.ProjectItems);

                foreach (var projectItem in projectitems)
                {
                    projectItemList.Add(projectItem);
                }
            }

            return projectItemList;
        }

        /// <summary>
        /// 获取类
        /// </summary>
        /// <param name="codeElements"></param>
        /// <returns></returns>
        private static CodeClass GetClass(CodeElements codeElements)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<CodeElement> elements = codeElements.Cast<CodeElement>().ToList();
            if (elements.FirstOrDefault(codeElement => { ThreadHelper.ThrowIfNotOnUIThread(); return codeElement.Kind == vsCMElement.vsCMElementClass; }) is CodeClass result)
            {
                return result;
            }
            foreach (var codeElement in elements)
            {
                result = GetClass(codeElement.Children);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        /// <summary>
        /// 获取当前所选文件去除项目目录后的文件夹结构
        /// </summary>
        /// <param name="selectProjectItem"></param>
        /// <returns></returns>
        private static string GetSelectFileDirPath(Project topProject, ProjectItem selectProjectItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dirPath = "";
            if (selectProjectItem != null)
            {
                //所选文件对应的路径
                string fileNames = selectProjectItem.FileNames[0];
                string selectedFullName = fileNames.Substring(0, fileNames.LastIndexOf('\\'));

                //所选文件所在的项目
                if (topProject != null)
                {
                    //项目目录
                    string projectFullName = topProject.FullName.Substring(0, topProject.FullName.LastIndexOf('\\'));

                    //当前所选文件去除项目目录后的文件夹结构
                    dirPath = selectedFullName.Replace(projectFullName, "");
                }
            }

            return dirPath.Substring(1);
        }

        /// <summary>
        /// 添加文件到项目中
        /// </summary>
        /// <param name="folder"></param>
        /// <param name="content"></param>
        /// <param name="fileName"></param>
        private void AddFileToProjectItem(ProjectItem folder, string content, string fileName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string path = Path.GetTempPath();
                Directory.CreateDirectory(path);
                string file = Path.Combine(path, fileName);
                File.WriteAllText(file, content, System.Text.Encoding.UTF8);
                try
                {
                    folder.ProjectItems.AddFromFileCopy(file);
                }
                finally
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// 添加文件到指定目录
        /// </summary>
        /// <param name="directoryPathOrFullPath"></param>
        /// <param name="content"></param>
        /// <param name="fileName"></param>
        private static void AddFileToDirectory(string directoryPathOrFullPath, string content, string fileName = "")
        {
            try
            {
                string file = string.IsNullOrEmpty(fileName) ? directoryPathOrFullPath : Path.Combine(directoryPathOrFullPath, fileName);
                File.WriteAllText(file, content, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {

            }
        }
        #endregion

    }
}
