using System;
using System.Collections;
using System.Collections.Generic;
using System.Web;
using System.Data;
using Oracle.ManagedDataAccess;
using Oracle.ManagedDataAccess.Client;
using System.Collections.Specialized;
using Oracle.ManagedDataAccess.Types;
using System.Text;
using System.IO;
using log4net;

namespace PLSQLGatewayModule
{
    /// <summary>
    /// Handles the database connection to Oracle and executes SQL and PL/SQL
    /// </summary>
    public class OracleInterface
    {
        private static readonly ILog logger = LogManager.GetLogger(typeof(PLSQLHttpModule));

        public const int PLSQL_MAX_STR_SIZE = 32767;
        public const int ORA_APPLICATION_ERROR_BEGIN = 20000;
        public const int ORA_APPLICATION_ERROR_END = 20999;
        
        private string _dadName = "";
        private DadConfiguration _dadConfig = null;
        private OracleParameterCache _opc = null;

        private string _connStr = "";
        private OracleConnection _conn;
        private OracleTransaction _txn;
        private string _lastError = "";
        private int _lastErrorCode = 0;

        private bool _moreToFetch = true;
        private bool _connected = false;

        private string _soapReturnValue = "";

        private string _wsdlBody = "";
        private string _soapBody = "";

        // cache of document-table column metadata, keyed by (upper-cased) table name,
        // so we only look it up once per connection instead of once per uploaded file
        private Dictionary<string, DocumentTableMetadata> _documentTableMetadataCache = new Dictionary<string, DocumentTableMetadata>();

        // cached probe result for the APEX gateway upload API (apex_util.set_blob):
        // 0 = not yet probed, 1 = available (signature cached below), -1 = not available
        private int _apexUploadApiState = 0;
        private string _apexUploadApiName = "";
        private List<string> _apexUploadApiParamNames = new List<string>();
        private List<string> _apexUploadApiParamTypes = new List<string>();

        /// <summary>
        /// Holds the set of columns that actually exist on a configured DocumentTableName,
        /// plus any NOT NULL columns without a default that we don't know how to populate.
        /// Used so that UploadFiles() can build an INSERT that matches whatever the real
        /// table looks like today, instead of a hardcoded column list that may be stale
        /// (this matters a lot for Oracle's internal WWV_FLOW_FILE_OBJECTS$ table, whose
        /// shape is undocumented and has changed across APEX releases).
        /// </summary>
        private class DocumentTableMetadata
        {
            public HashSet<string> Columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> RequiredColumnsWithoutValue = new List<string>();
        }

      public OracleInterface(GatewayRequest req, OracleParameterCache opc)
      {

          _dadName = req.DadName;
          _dadConfig = req.DadConfig;
          _opc = opc;

          string dbUsername = _dadConfig.DatabaseUserName;
          string dbPassword = _dadConfig.DatabasePassword;

          // Integrated Windows Authentication (use in combination with Oracle proxy authentication)
          if (dbUsername == "LOGON_USER")
          {
              dbUsername = req.WindowsUsername;
              // if username contains backslash (domain\user), add double quotes to username
              if (dbUsername.IndexOf("\\") > -1) {
                  dbUsername = "\"" + dbUsername + "\"";
              }
          }

          if (dbUsername == "LOGON_USER_NO_DOMAIN")
          {
              dbUsername = req.WindowsUsernameNoDomain;
          }
                    
          // for connection string attributes, see http://download.oracle.com/docs/html/E15167_01/featConnecting.htm#i1006259
          _connStr = "User Id=" + dbUsername + ";Password=" + dbPassword + ";Data Source=" + _dadConfig.DatabaseConnectString + ";" + _dadConfig.DatabaseConnectStringAttributes;
          
          // careful with this one, it will expose the passwords in the log
          // use it just for additional debugging during development
          // logger.Debug("Connection string: " + _connStr);

          // Connect to Oracle
          if (logger.IsDebugEnabled)
          {
              logger.Debug("Connecting with user " + dbUsername + " to " + _dadConfig.DatabaseConnectString + "...");
          }
          
          _conn = new OracleConnection(_connStr);

          try
          {
              _conn.Open();
              _connected = true;
              if (logger.IsDebugEnabled)
              {
                  logger.Debug("Connected to Oracle " + _conn.ServerVersion);
              }
          }
          catch (OracleException e)
          {
              _lastError = e.Message;
              logger.Error("Failed to connect to database: " + e.Message);
          }

          if (_connected)
          {
              _txn = _conn.BeginTransaction();

              // setup National Language Support (NLS)

              string sql = "alter session set nls_language='" + _dadConfig.NLSLanguage + "' nls_territory='" + _dadConfig.NLSTerritory + "'";
              ExecuteSQL(sql, new ArrayList());

              //OracleGlobalization glb = OracleGlobalization.GetThreadInfo();
              //logger.Debug ("ODP.NET Client Character Set: " + glb.ClientCharacterSet);

              // ensure a stateless environment by resetting package state
              sql = "begin dbms_session.modify_package_state(dbms_session.reinitialize); end;";
              ExecuteSQL(sql, new ArrayList());

              if (_dadConfig.InvocationProtocol == DadConfiguration.INVOCATION_PROTOCOL_SOAP)
              {
                  // use SOAP date encoding
                  sql = "alter session set nls_date_format = '" + _dadConfig.SoapDateFormat + "'";
                  ExecuteSQL(sql, new ArrayList());
              }

          }

      }

      public void DoCommit()
      {
          if (logger.IsDebugEnabled)
          {
              logger.Debug("Committing database transaction...");
          }
          
          _txn.Commit();

          if (logger.IsDebugEnabled)
          {
              logger.Debug("Commit completed.");
          }
      }

      public void DoRollback()
      {
          if (_connected)
          {
              logger.Debug("Rolling back database transaction...");
              _txn.Rollback();
              logger.Debug("Rollback completed.");
          }
      }

      public void CloseConnection()
      {
          if (logger.IsDebugEnabled)
          {
              logger.Debug("Closing database connection...");
          }
          
          _conn.Close();
          _conn.Dispose();

          if (logger.IsDebugEnabled)
          {
              logger.Debug("Database connection closed.");
          }
      }

      public string GetLastErrorText()
      {
          return _lastError;
      }

      public int GetLastErrorCode()
      {
          return _lastErrorCode;
      }
        
      public bool ExecuteSQL(string sql, ArrayList paramValues)
      {

          OracleCommand cmd = new OracleCommand(sql, _conn);

          int paramCount = 0;

          foreach (string s in paramValues)
          {
              paramCount = paramCount + 1;
              OracleParameter p = cmd.Parameters.Add("b" + paramCount.ToString(), OracleDbType.Varchar2, s, ParameterDirection.Input);
          }

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              cmd.ExecuteNonQuery();
              _lastError = "";
          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return false;
          }

          return true;

      }

      /// <summary>
      /// given an array of valid int values, return the first value equal to or above the given value
      /// </summary>
      /// <param name="size"></param>
      /// <param name="validSizes"></param>
      /// <returns></returns>
      private int GetNextValidSize(int size, int[] validSizes)
      {
          int returnValue = size;

          for (int i = 0; i < validSizes.Length; i++)
          {
              if (validSizes[i] >= size)
              {
                  returnValue = validSizes[i];
                  break;
              }
          }

          return returnValue;
      }

      public bool ExecuteMainProc(OwaProcedure owaProc, List<NameValuePair> paramList, bool describeProc, string procName)
      {

          int[] bindBucketLengths = _dadConfig.BindBucketLengths;
          int[] bindBucketWidths = _dadConfig.BindBucketWidths;

          NameValueCollection procParams = new NameValueCollection();
          string dataType = "";
          bool paramExists = false;

          string sql = "";

          int paramCount = 0;

          if (describeProc)
          {
              procParams = DescribeSingleProc(_dadName, procName);
              sql = owaProc.BuildSQLStatement(procParams, paramList);
          }
          else
          {
              sql = owaProc.BuildSQLStatement();
          }

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter pReturnValue = null;
          OracleParameter pIsDownload = null;
          
          if (owaProc.RequestValidationFunction.Length > 0)
          {
              OracleParameter pName = cmd.Parameters.Add("p_proc_name", OracleDbType.Varchar2, PLSQL_MAX_STR_SIZE, procName, ParameterDirection.Input);
          }

          if (owaProc.IsSoapRequest)
          {
              pReturnValue = cmd.Parameters.Add("p_return_value", OracleDbType.Clob, ParameterDirection.Output);
          }

          foreach (NameValuePair nvp in paramList)
          {

              if (describeProc)
              {
                  paramExists = (procParams.GetValues(nvp.Name) != null);
                  if (paramExists)
                  {
                      dataType = procParams.GetValues(nvp.Name)[0];
                  }
              }
              else
              {
                  paramExists = true;
                  dataType = "";
              }

              if (paramExists)
              {
                  paramCount = paramCount + 1;

                  if (nvp.ValueType == ValueType.ArrayValue || dataType == "PL/SQL TABLE")
                  {
                      OracleParameter p = cmd.Parameters.Add("b" + paramCount.ToString(), OracleDbType.Varchar2, ParameterDirection.Input);
                      p.CollectionType = OracleCollectionType.PLSQLAssociativeArray;
                      p.Value = nvp.ValuesAsArray;
                      //p.Size = nvp.Values.Count();
                      p.Size = GetNextValidSize(nvp.Values.Count, bindBucketLengths);
                  }
                  else
                  {
                      if (nvp.Value.Length > PLSQL_MAX_STR_SIZE || dataType == "CLOB")
                      {
                          OracleParameter p = cmd.Parameters.Add("b" + paramCount.ToString(), OracleDbType.Clob, nvp.Value.Length, nvp.Value, ParameterDirection.Input);
                      }
                      else
                      {
                          OracleParameter p = cmd.Parameters.Add("b" + paramCount.ToString(), OracleDbType.Varchar2, GetNextValidSize(nvp.Value.Length, bindBucketWidths), nvp.Value, ParameterDirection.Input);
                      }
                  }

              }
              else
              {
                  logger.Warn(string.Format("Mismatch between metadata ({0} parameters) and actual invocation ({1} parameters): Parameter '{2}' was not found in metadata, and was skipped to avoid errors.", procParams.Count, paramList.Count, nvp.Name));
              }

          }

          if (owaProc.CheckForDownload)
          {
              pIsDownload = cmd.Parameters.Add("p_is_download", OracleDbType.Int32, ParameterDirection.Output);
          }

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              cmd.ExecuteNonQuery();
              _lastError = "";
              _lastErrorCode = 0;
          }
          catch (OracleException e)
          {
              if (logger.IsDebugEnabled)
              {
                  logger.Debug("Command failed: " + e.Message);
              }
              _lastError = e.Message;
              _lastErrorCode = e.Number;
              return false;
          }

          if (owaProc.CheckForDownload)
          {
            int isDownload = (int)(OracleDecimal)pIsDownload.Value;
            IsDownload = (isDownload != 0);
            if (logger.IsDebugEnabled && IsDownload)
            {
                logger.Debug("  IsDownload = True");
            }
          }
          
          if (owaProc.IsSoapRequest)
          {

              if (pReturnValue.Status == OracleParameterStatus.NullFetched)
              {
                  _soapReturnValue = "";
              }
              else
              {
                  OracleClob tempClob = (OracleClob)pReturnValue.Value;
                  _soapReturnValue = tempClob.Value;
                  
              }
          }

          return true;

      }

        
      public bool SetupOwaCGI(List<NameValuePair> serverVariables, string hostName, string hostAddress, string basicAuthUsername, string basicAuthPassword)
      {

          // note: as of Thoth Gateway 1.3.7, the following elements have been removed as no longer relevant for "modern" usage of the gateway
          // * setting up the owa.ip_address record based on the client IP address (does not work with IPv6 addresses anyway)
          // * setting up the hostname, user id and password for basic authentication (the Apex Listener does not do this either)
          // * calling owa.initialize() before owa.init_cgi_env() (the Apex Listener does not do this either)
          
          // htbuf_len: reduce this limit based on your worst-case character size.
          // For most character sets, this will be 2 bytes per character, so the limit would be 127.
          // For UTF8 Unicode, it's 3 bytes per character, meaning the limit should be 85.
          // For the newer AL32UTF8 Unicode, it's 4 bytes per character, and the limit should be 63.

          string sql = "begin owa.init_cgi_env(:ecount, :namarr, :valarr); htp.init; htp.htbuf_len := 63; end;";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          // note: even though the parameters are named, ODP.NET maps the parameters to bind variables by position unless cmd.BindByName is true
          // see http://oradim.blogspot.com/2009/03/odpnet-tip-bind-variables-bindbyname.html

          OracleParameter Param1 = cmd.Parameters.Add("ecount", OracleDbType.Int32, ParameterDirection.Input);
          OracleParameter Param2 = cmd.Parameters.Add("namarr", OracleDbType.Varchar2, ParameterDirection.Input);
          OracleParameter Param3 = cmd.Parameters.Add("valarr", OracleDbType.Varchar2, ParameterDirection.Input);

          string[] paramNameArray = new string[serverVariables.Count];
          string[] paramValueArray = new string[serverVariables.Count];

          int count = 0;
          foreach (NameValuePair nvp in serverVariables)
          {
              paramNameArray[count] = nvp.Name;
              paramValueArray[count] = nvp.Value;
              count = count + 1;

              //logger.Debug("CGI param name = " + nvp.Name + ", value = " + nvp.Value);
          }

          Param1.Value = count;
          
          Param2.CollectionType = OracleCollectionType.PLSQLAssociativeArray;
          Param2.Value = paramNameArray;
          
          Param3.CollectionType = OracleCollectionType.PLSQLAssociativeArray;
          Param3.Value = paramValueArray;

          if (logger.IsDebugEnabled)
          {
              logger.Debug("Executing SQL: " + cmd.CommandText);
          }

          try
          {
              cmd.ExecuteNonQuery();
              _lastError = "";
          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return false;
          }

          return true;

      }

      public bool MoreToFetch()
      {
          return _moreToFetch;
      }

      public bool Connected()
      {
          return _connected;
      }

      public string GetOwaPageFragment()
      {
          int linesToFetch = _dadConfig.FetchBufferSize;
          int linesFetched = 0;

          StringBuilder pageFragment = new StringBuilder();

          string sql = "begin owa.get_page(:linearr, :nlines); end;";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter Param1 = cmd.Parameters.Add("linearr", OracleDbType.Varchar2, ParameterDirection.Output);
          OracleParameter Param2 = cmd.Parameters.Add("nlines", OracleDbType.Int32, ParameterDirection.InputOutput);

          int[] bindSizes = new int[linesToFetch];

          for (int i = 0; i < bindSizes.Length; i++)
          {
              bindSizes[i] = 256;
          }

          Param1.CollectionType = OracleCollectionType.PLSQLAssociativeArray;
          Param1.Value = null;
          Param1.Size = linesToFetch;
          Param1.ArrayBindSize = bindSizes;

          Param2.Value = linesToFetch;

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              cmd.ExecuteNonQuery();
              _lastError = "";

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
          }

          linesFetched = (int)((OracleDecimal)Param2.Value);

          if (linesFetched < linesToFetch)
          {
              _moreToFetch = false;
          }
          else
          {
              _moreToFetch = true;
          }

          //for (int i = 0; i < Param1.Size; i++)
          for (int i = 0; i < linesFetched; i++)
          {
              pageFragment.Append((Param1.Value as Array).GetValue(i));
          }
          
          return pageFragment.ToString();

      }


      public bool UploadFiles(List<UploadedFile> files, List<NameValuePair> requestParams)
      {

          logger.Debug(string.Format("Uploading {0} file(s)...", files.Count));

          string documentFilePath = _dadConfig.DocumentFilePath;
          string documentXdbPath = _dadConfig.DocumentXdbPath;

          string mimeType = "";

          for (int i = 0; i < files.Count; i++)
			{

              HttpPostedFile f = files[i].PostedFile;

              if (_dadConfig.DocumentMaxUploadSize > 0 && f.InputStream.Length > _dadConfig.DocumentMaxUploadSize)
              {
                  logger.Warn("File size of file " + f.FileName + " (" + f.InputStream.Length.ToString() + " bytes) exceeds allowed maximum (" + _dadConfig.DocumentMaxUploadSize.ToString() + " bytes), skipping upload");
              }
              else
              {

                  if (documentFilePath.Length > 0)
                  {

                      string fileLocation = documentFilePath + "\\" + files[i].UniqueFileName;
                      logger.Debug("Uploading to file system: " + fileLocation);

                      try
                      {
                          files[i].PostedFile.SaveAs(fileLocation);
                      }
                      catch (Exception e)
                      {
                          logger.Error("The SaveAs operation failed: " + e.Message);
                          _lastError = e.Message;
                          return false;
                      }
                  }
                  else if (documentXdbPath.Length > 0)
                  {
                      string resourceLocation = documentXdbPath + "/" + files[i].UniqueFileName;
                      logger.Debug("Uploading to XDB Repository: " + resourceLocation);

                      // read the file into a byte array
                      // NOTE: Stream.Read() is not guaranteed to fill the buffer in one call,
                      // especially for larger files -- read in a loop (or use ReadFully) to
                      // avoid silently truncating the upload.
                      byte[] fileData = ReadFully(f.InputStream);

                      // see http://stanford.edu/dept/itss/docs/oracle/10g/appdev.101/b10790/xdb19rpl.htm#i1028077
                      // see http://www.adp-gmbh.ch/ora/misc/globalization.html#char_sets

                      // TODO: why do we have to specify a character set for binary files (blobs) ?

                      string sql = "declare l_result boolean; begin l_result := dbms_xdb.createresource(:p_path, :p_data, nls_charset_id(:p_charset)); if l_result then :p_result := 1; else :p_result := 0; end if; end;";

                      OracleCommand cmd = new OracleCommand(sql, _conn);

                      OracleParameter p1 = cmd.Parameters.Add("p_path", OracleDbType.Varchar2, resourceLocation, ParameterDirection.Input);
                      OracleParameter p2 = cmd.Parameters.Add("p_data", OracleDbType.Blob, fileData, ParameterDirection.Input);
                      OracleParameter p3 = cmd.Parameters.Add("p_charset", OracleDbType.Varchar2, _dadConfig.NLSCharset, ParameterDirection.Input);
                      OracleParameter p4 = cmd.Parameters.Add("p_result", OracleDbType.Int32, ParameterDirection.Output);

                      logger.Debug("Executing SQL: " + cmd.CommandText);

                      try
                      {
                          cmd.ExecuteNonQuery();
                          _lastError = "";

                          int xdbResult = (int)(OracleDecimal)p4.Value;
                          return (xdbResult != 0);

                      }
                      catch (OracleException e)
                      {
                          logger.Error("Command failed: " + e.Message);
                          _lastError = e.Message;
                          return false;
                      }

                  }
                  else
                  {
                      // read the file into a byte array (see ReadFully() note above)
                      byte[] fileData = ReadFully(f.InputStream);

                      // use the mime type as defined on the server (see IIS or website settings) instead of the mime type submitted by the client
                      mimeType = System.Web.MimeMapping.GetMimeMapping(files[i].UniqueFileName);

                      logger.Debug("MIME type from request: " + f.ContentType);
                      logger.Debug("MIME type from file name: " + mimeType);

                      // Upload strategy (DocumentUploadMode DAD parameter):
                      //
                      //   "Auto"  (default) - use the APEX gateway upload API (apex_util.set_blob) when it
                      //                       is installed and executable, otherwise fall back to a direct
                      //                       insert into DocumentTableName. This mirrors what ORDS does:
                      //                       since ORDS 18.3, when APEX 5+ is detected, uploads go through
                      //                       apex_util.set_blob rather than a staging-table insert, and
                      //                       newer APEX versions expect this.
                      //   "Apex"  - require the APEX API; fail the upload if it is not available.
                      //   "Table" - legacy behavior; always insert directly into DocumentTableName.
                      //
                      // Note that apex_util.set_blob is not part of the documented APEX API Reference
                      // (there is no documented PL/SQL API for gateway-side uploads even in APEX 24.x),
                      // so its signature is described at runtime from ALL_ARGUMENTS and bound by name,
                      // rather than hardcoded, to survive signature changes between APEX releases.

                      string uploadMode = _dadConfig.DocumentUploadMode.ToLowerInvariant();

                      bool uploadOk;

                      if (uploadMode == "table")
                      {
                          logger.Debug("DocumentUploadMode=Table: uploading to database table with unique file name: " + files[i].UniqueFileName);
                          uploadOk = InsertIntoDocumentTable(files[i], fileData, mimeType, requestParams);
                      }
                      else
                      {
                          bool apiAvailable;
                          uploadOk = TryUploadViaApexApi(files[i], fileData, mimeType, requestParams, out apiAvailable);

                          if (!apiAvailable)
                          {
                              if (uploadMode == "apex")
                              {
                                  _lastError = "DocumentUploadMode=Apex, but no APEX gateway upload API (apex_util.set_blob) is available/executable for this database user.";
                                  logger.Error(_lastError);
                                  return false;
                              }

                              logger.Debug("APEX gateway upload API not available; falling back to direct insert into document table with unique file name: " + files[i].UniqueFileName);
                              uploadOk = InsertIntoDocumentTable(files[i], fileData, mimeType, requestParams);
                          }
                      }

                      if (!uploadOk)
                      {
                          return false;
                      }
                  }

              }

          }

          return true;

      }

      /// <summary>
      /// Reads an entire stream into a byte array. Stream.Read() is allowed by contract to
      /// return fewer bytes than requested even when more data is available (this is common
      /// for larger, network-backed HttpPostedFile streams), so a single Read() call -- as the
      /// original code used -- can silently truncate the uploaded file. This reads in a loop
      /// (via CopyTo) until the stream is exhausted.
      /// </summary>
      private byte[] ReadFully(Stream input)
      {
          if (input.CanSeek)
          {
              input.Position = 0;
          }

          using (MemoryStream ms = new MemoryStream())
          {
              input.CopyTo(ms);
              return ms.ToArray();
          }
      }

      private string GetParamValue(List<NameValuePair> requestParams, string name)
      {
          if (requestParams == null)
          {
              return "";
          }

          foreach (NameValuePair nvp in requestParams)
          {
              if (string.Equals(nvp.Name, name, StringComparison.OrdinalIgnoreCase))
              {
                  return nvp.Value;
              }
          }

          return "";
      }

      /// <summary>
      /// Probes (once per connection) for the APEX gateway upload API and caches its
      /// runtime-described signature. Candidates are tried in order:
      ///   1. apex_util.set_blob   (what ORDS 18.3+ calls when APEX 5+ is present)
      ///   2. apex_util.file_upload (older name checked for by ORDS at runtime)
      /// Returns true if a usable procedure was found.
      /// </summary>
      private bool ProbeApexUploadApi()
      {
          if (_apexUploadApiState != 0)
          {
              return (_apexUploadApiState == 1);
          }

          string[] candidates = new string[] { "apex_util.set_blob", "apex_util.file_upload" };

          foreach (string candidate in candidates)
          {
              string savedError = _lastError;

              OracleObjectInfo ooi = ResolveName(candidate);

              if (_lastError == "" && ooi.ObjectName != null && ooi.ObjectName.Length > 0)
              {
                  NameValueCollection procParams = GetProcParams(ooi.SchemaName, ooi.PackageName, ooi.ObjectName);

                  if (_lastError == "" && procParams.Count > 0)
                  {
                      // GetProcParams orders by (overload, sequence) but does not separate
                      // overloads; dedupe by name and keep the first occurrence of each,
                      // which corresponds to the first overload.
                      _apexUploadApiParamNames.Clear();
                      _apexUploadApiParamTypes.Clear();

                      foreach (string paramName in procParams)
                      {
                          if (!ListContainsIgnoreCase(_apexUploadApiParamNames, paramName))
                          {
                              _apexUploadApiParamNames.Add(paramName);
                              _apexUploadApiParamTypes.Add(procParams.GetValues(paramName)[0]);
                          }
                      }

                      _apexUploadApiName = candidate;
                      _apexUploadApiState = 1;

                      logger.Debug("APEX gateway upload API found: " + candidate + " (" + ooi.SchemaName + "." + ooi.PackageName + "." + ooi.ObjectName + "), parameters: " + string.Join(", ", _apexUploadApiParamNames.ToArray()));

                      return true;
                  }
              }

              // ResolveName/GetProcParams set _lastError on failure; a failed probe is not
              // an error for the request, so restore the previous error state
              _lastError = savedError;
          }

          logger.Debug("No APEX gateway upload API (apex_util.set_blob / apex_util.file_upload) found or executable for this database user.");
          _apexUploadApiState = -1;

          return false;
      }

      /// <summary>
      /// Uploads one file through the APEX gateway upload API (see ProbeApexUploadApi),
      /// binding the runtime-described parameters by name. Parameter mapping is heuristic
      /// because the procedure is undocumented; anything unmapped is bound as NULL (and
      /// logged) so procedure defaults apply where they exist.
      ///
      /// apiAvailable is set to false when no API procedure exists at all -- in that case
      /// the caller may fall back to the direct document-table insert. If the API exists
      /// but the call fails, apiAvailable is true and the method returns false, so the
      /// request fails visibly instead of silently switching storage semantics.
      /// </summary>
      private bool TryUploadViaApexApi(UploadedFile uploadedFile, byte[] fileData, string mimeType, List<NameValuePair> requestParams, out bool apiAvailable)
      {
          apiAvailable = ProbeApexUploadApi();

          if (!apiAvailable)
          {
              return false;
          }

          string flowId = GetParamValue(requestParams, "p_flow_id");

          StringBuilder callParams = new StringBuilder();
          List<OracleParameter> bindParams = new List<OracleParameter>();

          for (int i = 0; i < _apexUploadApiParamNames.Count; i++)
          {
              string paramName = _apexUploadApiParamNames[i];
              string upperName = paramName.ToUpperInvariant();
              string dataType = _apexUploadApiParamTypes[i].ToUpperInvariant();

              string bindName = "b" + (i + 1).ToString();
              OracleParameter p;

              if (dataType == "BLOB")
              {
                  p = new OracleParameter(bindName, OracleDbType.Blob);
                  p.Value = fileData;
              }
              else if (upperName.Contains("MIME"))
              {
                  p = new OracleParameter(bindName, OracleDbType.Varchar2);
                  p.Value = mimeType;
              }
              else if (upperName.Contains("FILENAME") || (upperName.Contains("FILE") && upperName.Contains("NAME")))
              {
                  p = new OracleParameter(bindName, OracleDbType.Varchar2);
                  p.Value = System.IO.Path.GetFileName(uploadedFile.FileName);
              }
              else if (upperName.Contains("ITEM"))
              {
                  // the form field (page item) name the file was posted under
                  p = new OracleParameter(bindName, OracleDbType.Varchar2);
                  p.Value = uploadedFile.ParamName;
              }
              else if (upperName == "P_NAME" || upperName.EndsWith("_NAME"))
              {
                  p = new OracleParameter(bindName, OracleDbType.Varchar2);
                  p.Value = uploadedFile.UniqueFileName;
              }
              else if (upperName.Contains("CHARSET"))
              {
                  p = new OracleParameter(bindName, OracleDbType.Varchar2);
                  p.Value = _dadConfig.NLSCharset;
              }
              else if (upperName.Contains("FLOW") || upperName.Contains("APPLICATION"))
              {
                  int flowIdInt;

                  if (flowId.Length > 0 && int.TryParse(flowId, out flowIdInt))
                  {
                      p = new OracleParameter(bindName, (dataType == "NUMBER") ? OracleDbType.Int32 : OracleDbType.Varchar2);
                      p.Value = (dataType == "NUMBER") ? (object)flowIdInt : (object)flowId;
                  }
                  else
                  {
                      p = new OracleParameter(bindName, OracleDbType.Varchar2);
                      p.Value = DBNull.Value;
                      logger.Warn("APEX upload API parameter '" + paramName + "' looks like an application id, but no p_flow_id was found on the request; binding NULL. If the call fails (e.g. ORA-20888), this is why.");
                  }
              }
              else
              {
                  // unknown parameter -- bind NULL so a procedure default (if any) applies
                  p = new OracleParameter(bindName, OracleDbType.Varchar2);
                  p.Value = DBNull.Value;
                  logger.Debug("APEX upload API parameter '" + paramName + "' (" + dataType + ") is not recognized by the gateway; binding NULL.");
              }

              p.Direction = ParameterDirection.Input;
              bindParams.Add(p);

              if (callParams.Length > 0)
              {
                  callParams.Append(", ");
              }

              callParams.Append(paramName + " => :" + bindName);
          }

          string sql = "begin " + _apexUploadApiName + " (" + callParams.ToString() + "); end;";

          OracleCommand cmd = new OracleCommand(sql, _conn);
          cmd.BindByName = true;

          foreach (OracleParameter p in bindParams)
          {
              cmd.Parameters.Add(p);
          }

          logger.Debug("Uploading via APEX gateway upload API. Executing SQL: " + cmd.CommandText);

          try
          {
              cmd.ExecuteNonQuery();
              _lastError = "";
              return true;
          }
          catch (OracleException e)
          {
              logger.Error("APEX gateway upload API call failed: " + e.Message);
              logger.Error("Signature used: " + _apexUploadApiName + " (" + string.Join(", ", _apexUploadApiParamNames.ToArray()) + "). If the parameter mapping is wrong for this APEX version, set DocumentUploadMode=Table in the DAD configuration to use the legacy direct insert instead.");
              _lastError = e.Message;
              return false;
          }
      }

      /// <summary>
      /// Looks up the column metadata (existing columns, and NOT NULL columns without a
      /// default) for a configured DocumentTableName, via ALL_TAB_COLUMNS. Cached per table
      /// name for the lifetime of this connection.
      ///
      /// This exists because UploadFiles() historically hardcoded a 7-column INSERT that
      /// matches the classic mod_plsql "document table" definition. When DocumentTableName
      /// points at Oracle's own internal APEX table (WWV_FLOW_FILE_OBJECTS$, as used for
      /// File Browse page items), that table has additional columns -- notably
      /// SECURITY_GROUP_ID and FLOW_ID, which scope the row to an APEX workspace/application
      /// and which APEX's own file-handling relies on to find the row again -- that the
      /// hardcoded INSERT never populated. Because that internal table's shape is
      /// undocumented and has changed across APEX releases, we look it up at runtime instead
      /// of guessing.
      /// </summary>
      private DocumentTableMetadata GetDocumentTableMetadata(string tableName)
      {
          string cacheKey = tableName.ToUpperInvariant();

          if (_documentTableMetadataCache.ContainsKey(cacheKey))
          {
              return _documentTableMetadataCache[cacheKey];
          }

          DocumentTableMetadata meta = new DocumentTableMetadata();

          string schema = "";
          string table = tableName;
          int dotPos = tableName.IndexOf('.');

          if (dotPos > -1)
          {
              schema = tableName.Substring(0, dotPos).Trim('"');
              table = tableName.Substring(dotPos + 1);
          }

          table = table.Trim('"');

          string sql = (schema.Length > 0)
              ? "select column_name, nullable, data_default from all_tab_columns where owner = upper(:p_owner) and table_name = upper(:p_table)"
              : "select column_name, nullable, data_default from all_tab_columns where table_name = upper(:p_table)";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          if (schema.Length > 0)
          {
              cmd.Parameters.Add("p_owner", OracleDbType.Varchar2, schema, ParameterDirection.Input);
          }

          cmd.Parameters.Add("p_table", OracleDbType.Varchar2, table, ParameterDirection.Input);

          try
          {
              OracleDataReader dr = cmd.ExecuteReader();

              while (dr.Read())
              {
                  string colName = dr[0].ToString();
                  string nullable = dr[1].ToString();
                  bool hasDefault = !dr.IsDBNull(2);

                  meta.Columns.Add(colName);

                  if (nullable == "N" && !hasDefault)
                  {
                      meta.RequiredColumnsWithoutValue.Add(colName);
                  }
              }
          }
          catch (OracleException e)
          {
              logger.Warn("Could not look up column metadata for document table '" + tableName + "' via ALL_TAB_COLUMNS: " + e.Message);
          }

          if (meta.Columns.Count == 0)
          {
              logger.Warn("No columns found for document table '" + tableName + "' -- check that DocumentTableName is correct, that the table exists, and that the gateway's database user (" + _dadConfig.DatabaseUserName + ") has SELECT privilege on ALL_TAB_COLUMNS as well as SELECT/INSERT on the table itself. Falling back to the classic document-table column set.");
          }

          _documentTableMetadataCache[cacheKey] = meta;

          return meta;
      }

      /// <summary>
      /// Resolves an APEX workspace's SECURITY_GROUP_ID from the application (flow) id posted
      /// with the request. In Oracle APEX, security_group_id and workspace_id are the same
      /// value, and every row in WWV_FLOW_FILE_OBJECTS$ must carry the correct
      /// security_group_id or it will never be visible to the application via
      /// APEX_APPLICATION_FILES / APEX_APPLICATION_TEMP_FILES.
      /// Returns 0 if it could not be resolved.
      /// </summary>
      private int GetApexSecurityGroupId(string flowId)
      {
          int applicationId;

          if (!int.TryParse(flowId, out applicationId))
          {
              return 0;
          }

          string sql = "select workspace_id from apex_applications where application_id = :p_app_id";

          OracleCommand cmd = new OracleCommand(sql, _conn);
          cmd.Parameters.Add("p_app_id", OracleDbType.Int32, applicationId, ParameterDirection.Input);

          try
          {
              OracleDataReader dr = cmd.ExecuteReader();

              if (dr.Read())
              {
                  return Convert.ToInt32(dr[0]);
              }

              logger.Warn("Could not find application " + applicationId + " in APEX_APPLICATIONS while resolving security_group_id for file upload.");
              return 0;
          }
          catch (OracleException e)
          {
              logger.Warn("Failed to resolve security_group_id for application " + applicationId + ": " + e.Message);
              return 0;
          }
      }

      /// <summary>
      /// Inserts one uploaded file into the DAD's configured DocumentTableName. The column
      /// list is built dynamically from the table's actual current columns (see
      /// GetDocumentTableMetadata) rather than a hardcoded list, and -- when the table looks
      /// like APEX's internal WWV_FLOW_FILE_OBJECTS$ table (i.e. it has FLOW_ID and/or
      /// SECURITY_GROUP_ID columns) -- also populates flow_id/security_group_id from the
      /// posted p_flow_id, which APEX needs in order to associate the uploaded file with the
      /// correct application/workspace and pick it up into APEX_APPLICATION_TEMP_FILES.
      /// </summary>
      private bool InsertIntoDocumentTable(UploadedFile uploadedFile, byte[] fileData, string mimeType, List<NameValuePair> requestParams)
      {
          string tableName = _dadConfig.DocumentTableName;
          DocumentTableMetadata meta = GetDocumentTableMetadata(tableName);

          // if we couldn't determine the table's columns at all (e.g. no privilege on
          // ALL_TAB_COLUMNS), fall back to the original hardcoded classic document-table
          // column set rather than failing outright
          bool haveMetadata = (meta.Columns.Count > 0);

          List<string> colNames = new List<string>();
          List<string> colExpressions = new List<string>();
          List<OracleParameter> colParams = new List<OracleParameter>();

          // name (mandatory in both the classic document table and WWV_FLOW_FILE_OBJECTS$)
          AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "name", "p_name", OracleDbType.Varchar2, uploadedFile.UniqueFileName);

          // filename -- present on WWV_FLOW_FILE_OBJECTS$ (the original client-supplied name), harmless to omit on a classic doc table that lacks it
          AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "filename", "p_filename", OracleDbType.Varchar2, System.IO.Path.GetFileName(uploadedFile.FileName));

          AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "mime_type", "p_mime_type", OracleDbType.Varchar2, mimeType);
          AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "doc_size", "p_doc_size", OracleDbType.Int32, fileData.Length);
          AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "dad_charset", "p_dad_charset", OracleDbType.Varchar2, _dadConfig.NLSCharset);
          AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "content_type", "p_content_type", OracleDbType.Varchar2, "BLOB");
          AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "blob_content", "p_blob_content", OracleDbType.Blob, fileData);

          // last_updated is a SQL literal (sysdate), not a bind parameter
          if (!haveMetadata || meta.Columns.Contains("last_updated"))
          {
              colNames.Add("last_updated");
              colExpressions.Add("sysdate");
          }

          // APEX-specific scoping columns: only relevant (and only present) when
          // DocumentTableName points at APEX's internal file table
          if (haveMetadata && (meta.Columns.Contains("flow_id") || meta.Columns.Contains("security_group_id")))
          {
              string flowId = GetParamValue(requestParams, "p_flow_id");

              if (flowId.Length == 0)
              {
                  logger.Warn("Document table '" + tableName + "' looks like APEX's internal file table (it has a FLOW_ID/SECURITY_GROUP_ID column), but no p_flow_id was found on the request, so flow_id/security_group_id cannot be set. The uploaded file will likely not be visible to the APEX application.");
              }
              else
              {
                  int flowIdInt;

                  if (meta.Columns.Contains("flow_id") && int.TryParse(flowId, out flowIdInt))
                  {
                      AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "flow_id", "p_flow_id", OracleDbType.Int32, flowIdInt);
                  }

                  if (meta.Columns.Contains("security_group_id"))
                  {
                      int sgId = GetApexSecurityGroupId(flowId);

                      if (sgId > 0)
                      {
                          AddDocumentTableColumn(colNames, colExpressions, colParams, meta, haveMetadata, "security_group_id", "p_security_group_id", OracleDbType.Int32, sgId);
                      }
                      else
                      {
                          logger.Warn("Could not resolve security_group_id for APEX application " + flowId + " -- the uploaded file may not be visible to the application.");
                      }
                  }
              }
          }

          // warn (but don't necessarily fail) about any NOT NULL columns we didn't populate --
          // this turns a mysterious ORA-01400 into an actionable log message
          if (haveMetadata)
          {
              foreach (string requiredCol in meta.RequiredColumnsWithoutValue)
              {
                  if (!ListContainsIgnoreCase(colNames, requiredCol))
                  {
                      logger.Warn("Document table '" + tableName + "' has a NOT NULL column '" + requiredCol + "' with no default, which this gateway does not know how to populate. The insert below may fail with ORA-01400 because of it.");
                  }
              }
          }

          if (colNames.Count == 0)
          {
              _lastError = "No usable columns found for document table '" + tableName + "'";
              logger.Error(_lastError);
              return false;
          }

          string sql = "insert into " + tableName + " (" + string.Join(", ", colNames.ToArray()) + ") values (" + string.Join(", ", colExpressions.ToArray()) + ")";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          foreach (OracleParameter p in colParams)
          {
              cmd.Parameters.Add(p);
          }

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              cmd.ExecuteNonQuery();
              _lastError = "";
              return true;
          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return false;
          }
      }

      private bool ListContainsIgnoreCase(List<string> list, string value)
      {
          foreach (string s in list)
          {
              if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
              {
                  return true;
              }
          }

          return false;
      }

      /// <summary>
      /// Adds a bound column/value pair to the running INSERT lists, but only if the column
      /// either (a) is known to exist on the target table, or (b) we have no metadata at all
      /// for the table (in which case we fall back to the original hardcoded behavior and
      /// include it unconditionally).
      /// </summary>
      private void AddDocumentTableColumn(List<string> colNames, List<string> colExpressions, List<OracleParameter> colParams, DocumentTableMetadata meta, bool haveMetadata, string columnName, string paramName, OracleDbType dbType, object value)
      {
          if (haveMetadata && !meta.Columns.Contains(columnName))
          {
              return;
          }

          colNames.Add(columnName);
          colExpressions.Add(":" + paramName);

          OracleParameter p = new OracleParameter(paramName, dbType);
          p.Value = value;
          p.Direction = ParameterDirection.Input;
          colParams.Add(p);
      }

      public bool IsDownload
      {
          get;
          set;
      }

      public string GetDownloadInfo()
      {

          string sql = "begin wpg_docload.get_download_file (:p_download_info); end;";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter p = cmd.Parameters.Add("p_download_info", OracleDbType.Varchar2, ParameterDirection.Output);

          p.Size = 4000;

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              cmd.ExecuteNonQuery();

              string downloadInfo = (string)(OracleString)p.Value;

              logger.Debug("  download info = " + downloadInfo);

              _lastError = "";

              return downloadInfo;

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return "";
          }

      }


      public byte[] GetDownloadFile(string fileType)
      {

          string sql = "";
          OracleDbType fileParameterType = OracleDbType.Blob;

          if (fileType == "B")
          {
              sql = "begin wpg_docload.get_download_blob (:b1); end;";
              fileParameterType = OracleDbType.Blob;
          }
          else if (fileType == "F")
          {
              sql = "begin wpg_docload.get_download_bfile (:b1); end;";
              fileParameterType = OracleDbType.BFile;
          }

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter p = cmd.Parameters.Add("b1", fileParameterType, ParameterDirection.Output);

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              cmd.ExecuteNonQuery();

              byte[] byteData = new byte[0];

              // fetch the value of Oracle parameter into the byte array
              byteData = (byte[])((OracleBlob)(p.Value)).Value;

              _lastError = "";

              return byteData;

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return new byte[0];
          }

      }

      public byte[] GetDownloadFileFromDocTable(string fileName)
      {

          string sql = "select blob_content from " + _dadConfig.DocumentTableName + " where name = :b1";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter p = cmd.Parameters.Add("b1", OracleDbType.Varchar2, 256, fileName, ParameterDirection.Input);

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {

              OracleDataReader dr = cmd.ExecuteReader();

              byte[] byteData = new byte[0];

              while (dr.Read())
              {
                  OracleBlob blob = dr.GetOracleBlob(0);
                  byteData = (byte[])blob.Value;
              }

              _lastError = "";

              return byteData;

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return new byte[0];
          }

      }

      private OracleObjectInfo ResolveName(string procName)
      {
          OracleObjectInfo ooi = new OracleObjectInfo();

          logger.Debug("Attempting to resolve name " + procName);

          string sql = "begin dbms_utility.name_resolve (:p_name, 1, :p_schema, :p_part1, :p_part2, :p_dblink, :p_part1_type, :p_object_number); end;";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter p1 = cmd.Parameters.Add("p_name", OracleDbType.Varchar2, ParameterDirection.Input);
          p1.Value = procName;

          OracleParameter p2 = cmd.Parameters.Add("p_schema", OracleDbType.Varchar2, ParameterDirection.Output);
          p2.Size = 30;
          OracleParameter p3 = cmd.Parameters.Add("p_part1", OracleDbType.Varchar2, ParameterDirection.Output);
          p3.Size = 30;
          OracleParameter p4 = cmd.Parameters.Add("p_part2", OracleDbType.Varchar2, ParameterDirection.Output);
          p4.Size = 30;
          OracleParameter p5 = cmd.Parameters.Add("p_dblink", OracleDbType.Varchar2, ParameterDirection.Output);
          p5.Size = 30;
          OracleParameter p6 = cmd.Parameters.Add("p_part1_type", OracleDbType.Varchar2, ParameterDirection.Output);
          p6.Size = 30;
          OracleParameter p7 = cmd.Parameters.Add("p_object_number", OracleDbType.Int32, ParameterDirection.Output);

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              cmd.ExecuteNonQuery();
              _lastError = "";


              if (p2.Status == OracleParameterStatus.NullFetched)
              {
                  ooi.SchemaName = "";
              }
              else
              {
                  ooi.SchemaName = (string)(OracleString)p2.Value;
              }
              
              if (p3.Status == OracleParameterStatus.NullFetched)
              {
                  ooi.PackageName = "";
              }
              else
              {
                  ooi.PackageName = (string)(OracleString)p3.Value;
              }

              if (p4.Status == OracleParameterStatus.NullFetched)
              {
                  ooi.ObjectName = "";
              }
              else
              {
                  ooi.ObjectName = (string)(OracleString)p4.Value;
              }
              
              ooi.ObjectType = (string)(OracleString)p6.Value;
              ooi.ObjectId = (int)(OracleDecimal)p7.Value;

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
          }

          return ooi;

      }

      private List<string> GetPackageFunctions(string schemaName, string packageName)
      {
          List<string> functions = new List<string>();

          string sql = "select distinct object_name from all_arguments where owner = :p_owner and package_name = :p_package_name and position = 0 order by 1";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          cmd.CommandText = sql;
          cmd.Parameters.Clear();
          cmd.Parameters.Add("p_owner", OracleDbType.Varchar2, schemaName, ParameterDirection.Input);
          cmd.Parameters.Add("p_package_name", OracleDbType.Varchar2, packageName, ParameterDirection.Input);

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              OracleDataReader dr = cmd.ExecuteReader();

              while (dr.Read())
              {
                  functions.Add(dr[0].ToString());
              }

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return functions;
          }

          return functions;
      }

      private NameValueCollection GetProcParams(string schemaName, string packageName, string objectName)
      {
          NameValueCollection procParams = new NameValueCollection();

          string sql = "";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          if (packageName == "")
          {
              sql = "select argument_name, data_type from all_arguments where owner = :p_owner and package_name is null and object_name = :p_object_name and argument_name is not null order by overload, sequence";

              cmd.CommandText = sql;
              cmd.Parameters.Clear();
              cmd.Parameters.Add("p_owner", OracleDbType.Varchar2, schemaName, ParameterDirection.Input);
              cmd.Parameters.Add("p_object_name", OracleDbType.Varchar2, objectName, ParameterDirection.Input);

          }
          else
          {
              sql = "select argument_name, data_type from all_arguments where owner = :p_owner and package_name = :p_package_name and object_name = :p_object_name and argument_name is not null order by overload, sequence";

              cmd.CommandText = sql;
              cmd.Parameters.Clear();
              cmd.Parameters.Add("p_owner", OracleDbType.Varchar2, schemaName, ParameterDirection.Input);
              cmd.Parameters.Add("p_package_name", OracleDbType.Varchar2, packageName, ParameterDirection.Input);
              cmd.Parameters.Add("p_object_name", OracleDbType.Varchar2, objectName, ParameterDirection.Input);

          }

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {
              OracleDataReader dr = cmd.ExecuteReader();

              while (dr.Read())
              {
                  procParams.Add(dr[0].ToString(), dr[1].ToString());
              }


          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return procParams;
          }

          return procParams;

      }
        
      private NameValueCollection DescribeSingleProc(string dadName, string procName)
      {

          NameValueCollection procParams = _opc.GetParamsFromCache(dadName, procName);

          if (procParams.Count == 0)
          {
              logger.Debug("Procedure metadata not in cache, looking it up from database...");

              OracleObjectInfo ooi = ResolveName(procName);

              if (_lastError != "")
              {
                  return procParams; 
              }

              procParams = GetProcParams(ooi.SchemaName, ooi.PackageName, ooi.ObjectName);

              if (_lastError != "")
              {
                  return procParams;
              }

              _opc.SetParamsInCache(dadName, procName, procParams);

          }
          else
          {
              logger.Debug("Found procedure metadata in cache.");
          }

          return procParams;

      }

      private string GetSoapDataType(string oracleDatatype)
      {

          // see http://www.w3.org/TR/xmlschema-2/#built-in-primitive-datatypes
          
          switch (oracleDatatype)
          {
              case "CHAR":
              case "VARCHAR":
              case "VARCHAR2":
              case "CLOB":
                  return "string";
              case "NUMBER":
                  return "double";
              case "INTEGER":
                  return "int";
              case "DATE":
                  return "dateTime";
              default:
                  return "string";
          }
      }

      public bool GenerateWsdl(string serverName, string moduleName, string dadName, string procName)
      {
          OracleObjectInfo ooi = ResolveName(procName);

          List<string> functionList = new List<string>();

          string serviceName = "";

          if (ooi.PackageName.Length > 0 && ooi.ObjectName.Length == 0)
          {
              // get all the functions in the package, and get their parameters
              functionList = GetPackageFunctions(ooi.SchemaName, ooi.PackageName);
              serviceName = StringUtil.PrettyStr(ooi.PackageName);
          }
          else
          {
              functionList.Add(ooi.ObjectName);
              serviceName = StringUtil.PrettyStr(procName) + "Service";
          }
          
          NameValueCollection procParams = null;

          StringBuilder sb = new StringBuilder();

          sb.Append("<?xml version='1.0' encoding='utf-8'?>");
          sb.AppendFormat("<wsdl:definitions xmlns:soap='http://schemas.xmlsoap.org/wsdl/soap/' xmlns:soapenc='http://schemas.xmlsoap.org/soap/encoding/' xmlns:mime='http://schemas.xmlsoap.org/wsdl/mime/' xmlns:tns='{0}' xmlns:s='http://www.w3.org/2001/XMLSchema' xmlns:soap12='http://schemas.xmlsoap.org/wsdl/soap12/' xmlns:http='http://schemas.xmlsoap.org/wsdl/http/' targetNamespace='{0}' xmlns:wsdl='http://schemas.xmlsoap.org/wsdl/' >", _dadConfig.SoapTargetNamespace);

          sb.Append("<wsdl:types>");

          sb.AppendFormat("<s:schema elementFormDefault='qualified' targetNamespace='{0}'>", _dadConfig.SoapTargetNamespace);

          foreach (string functionName in functionList)
          {
              string prettyProcName = StringUtil.PrettyStr(functionName);

              procParams = _opc.GetParamsFromCache(dadName, ooi.PackageName + "." + functionName);
              if (procParams.Count == 0)
              {
                  procParams = GetProcParams(ooi.SchemaName, ooi.PackageName, functionName);
                  _opc.SetParamsInCache(dadName, ooi.PackageName + "." + functionName, procParams);
              }
              
              sb.AppendFormat("<s:element name='{0}'>", prettyProcName);
              sb.Append("<s:complexType>");
              sb.Append("<s:sequence>");
              foreach (string s in procParams)
              {
                  sb.AppendFormat("<s:element minOccurs='1' maxOccurs='1' name='{0}' type='s:{1}' />", s.ToLower(), GetSoapDataType(procParams.GetValues(s)[0]));
              }
              sb.Append("</s:sequence>");
              sb.Append("</s:complexType>");
              sb.Append("</s:element>");

              sb.AppendFormat("<s:element name='{0}Response'>", prettyProcName);
              sb.Append("<s:complexType>");
              sb.Append("<s:sequence>");
              // for now, we only support returning one (string) value
              sb.AppendFormat("<s:element minOccurs='1' maxOccurs='1' name='{0}Result' nillable='true' type='s:string' />", prettyProcName);
              sb.Append("</s:sequence>");
              sb.Append("</s:complexType>");
              sb.Append("</s:element>");
              
          }

          sb.Append("</s:schema>");
          sb.Append("</wsdl:types>");

          foreach (string functionName in functionList)
          {
              string prettyProcName = StringUtil.PrettyStr(functionName);

              sb.AppendFormat("<wsdl:message name='{0}SoapIn'>", prettyProcName);
              sb.AppendFormat("<wsdl:part name='parameters' element='tns:{0}' />", prettyProcName);
              sb.Append("</wsdl:message>");

              sb.AppendFormat("<wsdl:message name='{0}SoapOut'>", prettyProcName);
              sb.AppendFormat("<wsdl:part name='parameters' element='tns:{0}Response' />", prettyProcName);
              sb.Append("</wsdl:message>");

          }

          sb.AppendFormat("<wsdl:portType name='{0}Soap'>", serviceName);
         
          foreach (string functionName in functionList)
          {
              string prettyProcName = StringUtil.PrettyStr(functionName);

              sb.AppendFormat("<wsdl:operation name='{0}'>", prettyProcName);
              sb.AppendFormat("<wsdl:input message='tns:{0}SoapIn' />", prettyProcName);
              sb.AppendFormat("<wsdl:output message='tns:{0}SoapOut' />", prettyProcName);
              sb.Append("</wsdl:operation>");

          }

          sb.Append("</wsdl:portType>");

          sb.AppendFormat("<wsdl:binding name='{0}Soap' type='tns:{0}Soap'>", serviceName);

          foreach (string functionName in functionList)
          {
              string prettyProcName = StringUtil.PrettyStr(functionName);

              sb.Append("<soap:binding transport='http://schemas.xmlsoap.org/soap/http' />");
              sb.AppendFormat("<wsdl:operation name='{0}'>", prettyProcName);
              sb.AppendFormat("<soap:operation soapAction='{0}/{1}' style='document' />", _dadConfig.SoapTargetNamespace, prettyProcName);
              sb.Append("<wsdl:input>");
              sb.Append("<soap:body use='literal' />");
              sb.Append("</wsdl:input>");
              sb.Append("<wsdl:output>");
              sb.Append("<soap:body use='literal' />");
              sb.Append("</wsdl:output>");
              sb.Append("</wsdl:operation>");

          }

          sb.Append("</wsdl:binding>");

          sb.AppendFormat("<wsdl:service name='{0}'>", serviceName);
          sb.AppendFormat("<wsdl:port name='{0}Soap' binding='tns:{0}Soap'>", serviceName);

          if (ooi.PackageName.Length > 0)
          {
              sb.AppendFormat("<soap:address location='http://{0}/{1}/{2}/{3}' />", serverName, moduleName, dadName, ooi.PackageName.ToLower());
          }
          else
          {
              sb.AppendFormat("<soap:address location='http://{0}/{1}/{2}/{3}' />", serverName, moduleName, dadName, procName);
          }
          sb.Append("</wsdl:port>");
          sb.Append("</wsdl:service>");

          sb.Append("</wsdl:definitions>");

          _wsdlBody = sb.ToString();
          return true;
      }

      public void GenerateSoapFault(int errorCode, string errorText)
      {
          string soapFaultStyle = _dadConfig.SoapFaultStyle;
          string soapFaultStringTag = _dadConfig.SoapFaultStringTag;
          string soapFaultDetailTag = _dadConfig.SoapFaultDetailTag;

          string faultString = "";
          string faultDetail = "";

          StringBuilder sb = new StringBuilder();

          sb.Append("<?xml version='1.0' encoding='utf-8'?>");
          sb.Append("<soap:Envelope xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:xsd='http://www.w3.org/2001/XMLSchema' xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'>");
          sb.Append("<soap:Body>");
          sb.Append("<soap:Fault>");
          if (IsApplicationError(errorCode))
          {
              logger.Debug("User-defined application error, code " + errorCode + ", returning SOAP Fault as Client type.");

              if (soapFaultStyle == "Raw")
              {
                  faultString = errorCode.ToString();
                  faultDetail = errorText;
              }
              else if (soapFaultStyle == "UserFriendly")
              {
                  faultString = StringUtil.GetTagValue(errorText, soapFaultStringTag, "The request was rejected with server error code " + errorCode.ToString() + ".");
                  faultDetail = StringUtil.GetTagValue(errorText, soapFaultDetailTag, "See server logs for details.");
              }
              else
              {
                  faultString = "Invalid request was rejected by server.";
                  faultDetail = "See server logs for details.";
              }

              sb.Append("<faultcode>soap:Client</faultcode>");
              sb.AppendFormat("<faultstring>{0}</faultstring>", faultString);
              sb.AppendFormat("<detail>{0}</detail>", HttpUtility.HtmlEncode(faultDetail));
          }
          else
          {
              logger.Error("Unhandled Oracle error, code " + errorCode + ", returning SOAP Fault as Server type.");

              if (soapFaultStyle == "Raw")
              {
                  faultString = errorCode.ToString();
                  faultDetail = errorText;
              }
              else if (soapFaultStyle == "UserFriendly")
              {
                  faultString = "The server encountered a problem (server error code " + errorCode.ToString() + ") during processing of the request.";
                  faultDetail = "See server logs for details.";
              }
              else
              {
                  faultString = "Unhandled server error.";
                  faultDetail = "See server logs for details.";
              }

              sb.Append("<faultcode>soap:Server</faultcode>");
              sb.AppendFormat("<faultstring>{0}</faultstring>", faultString);
              sb.AppendFormat("<detail>{0}</detail>", HttpUtility.HtmlEncode(faultDetail));
          }
          sb.Append("</soap:Fault>");
          sb.Append("</soap:Body>");
          sb.Append("</soap:Envelope>");

          _soapBody = sb.ToString();
      }

      public void GenerateSoapResponse(string procName)
      {
          BuildSoapResponse(procName, _soapReturnValue);
      }

      private void BuildSoapResponse(string procName, string resultValue)
      {
          string prettyProcName = "";

          int startPos = procName.IndexOf(".");

          if (startPos > -1)
          {
              // just get the last part (the actual function name)
              prettyProcName = StringUtil.PrettyStr(procName.Substring(startPos + 1));
          }
          else
          {
              prettyProcName = StringUtil.PrettyStr(procName);
          }

          StringBuilder sb = new StringBuilder();

          sb.Append("<?xml version='1.0' encoding='utf-8'?>");
          sb.Append("<soap:Envelope xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:xsd='http://www.w3.org/2001/XMLSchema' xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'>");
          sb.Append("<soap:Body>");
          sb.AppendFormat("<{0}Response xmlns='{1}'>", prettyProcName, _dadConfig.SoapTargetNamespace);
          if (resultValue.Length > 0)
          {
              sb.AppendFormat("<{0}Result>{1}</{0}Result>", prettyProcName, HttpUtility.HtmlEncode(resultValue));
          }
          else
          {
              sb.AppendFormat("<{0}Result xsi:nil='true' />", prettyProcName); // since zero-length strings are the same as NULLs in Oracle... 
          }
          sb.AppendFormat("</{0}Response>", prettyProcName);
          sb.Append("</soap:Body>");
          sb.Append("</soap:Envelope>");

          _soapBody = sb.ToString();
      
      }

      public string WsdlBody()
      {
          return _wsdlBody;
      }

      public string SoapBody()
      {
          return _soapBody;
      }

      public string XdbContentType
      {
          get;
          set;
      }

      public string XdbResourceName
      {
          get;
          set;
      }
        
      public bool GetXdbResource(string resourceName)
      {

          XdbResourceName = _dadConfig.XdbAliasRoot + "/" + resourceName;

          logger.Debug("Getting XDB resource metadata for " + XdbResourceName);

          string sql = "select xdburitype(:b1).getContentType() from dual";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter p = cmd.Parameters.Add("b1", OracleDbType.Varchar2, 2000, XdbResourceName, ParameterDirection.Input);

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {

              OracleDataReader dr = cmd.ExecuteReader();

              while (dr.Read())
              {
                  OracleString str = dr.GetOracleString(0);
                  XdbContentType = (string)str.Value;
                  logger.Debug("XDB ContentType is " + XdbContentType);
              }

              _lastError = "";

              return true;

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return false;
          }


      }

      public byte[] GetXdbResourceFile()
      {

          logger.Debug("Getting XDB resource " + XdbResourceName);

          string sql = "select xdburitype(:b1).getBlob() from dual";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter p = cmd.Parameters.Add("b1", OracleDbType.Varchar2, 2000, XdbResourceName, ParameterDirection.Input);

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {

              OracleDataReader dr = cmd.ExecuteReader();

              byte[] byteData = new byte[0];

              while (dr.Read())
              {
                  OracleBlob blob = dr.GetOracleBlob(0);
                  byteData = (byte[])blob.Value;
              }

              _lastError = "";

              return byteData;

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return new byte[0];
          }

      }


      public string GetXdbResourceText()
      {

          logger.Debug("Getting XDB resource " + XdbResourceName);

          string sql = "select xdburitype(:b1).getClob() from dual";

          OracleCommand cmd = new OracleCommand(sql, _conn);

          OracleParameter p = cmd.Parameters.Add("b1", OracleDbType.Varchar2, 2000, XdbResourceName, ParameterDirection.Input);

          logger.Debug("Executing SQL: " + cmd.CommandText);

          try
          {

              OracleDataReader dr = cmd.ExecuteReader();

              string s = "";

              while (dr.Read())
              {
                  OracleClob clob = dr.GetOracleClob(0);
                  s = (string)clob.Value;
              }

              _lastError = "";

              return s;

          }
          catch (OracleException e)
          {
              logger.Error("Command failed: " + e.Message);
              _lastError = e.Message;
              return "";
          }

      }

      public static Boolean IsApplicationError(int errorCode)
      {
          if (errorCode >= ORA_APPLICATION_ERROR_BEGIN && errorCode <= ORA_APPLICATION_ERROR_END)
          {
              return true;
          }
          else
          {
              return false;
          }
      }

    }

}
