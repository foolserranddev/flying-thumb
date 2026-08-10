#include "fileserver.h"
#include <Arduino.h>
#include <ArduinoJson.h>
#include <DNSServer.h>
#include <ESPmDNS.h>
#include <Preferences.h>
#include <SD_MMC.h>
#include <Update.h>
#include <WebServer.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <esp_wps.h>
#include <esp_wifi.h>
#include <esp_ota_ops.h>
#include <esp_heap_caps.h>
#include "board_config.h"
#include "display.h"
#include "ota_health.h"
#include "usb_disk.h"

extern const uint8_t index_html_start[] asm("_binary_web_index_html_start");
extern const uint8_t index_html_end[] asm("_binary_web_index_html_end");
extern const uint8_t settings_html_start[] asm("_binary_web_settings_html_start");
extern const uint8_t settings_html_end[] asm("_binary_web_settings_html_end");

namespace {
constexpr uint16_t DISCOVERY_PORT=4210;
constexpr uint16_t DNS_PORT=53;
constexpr char DISCOVERY_REQUEST[]="FLYINGTHUMB_DISCOVER_V1";
constexpr char FIRMWARE_VERSION_BASE[]="2.4.7";
constexpr uint32_t WPS_PAIRING_WINDOW_MS=120000;
const IPAddress SETUP_IP(192,168,77,1);
const IPAddress SETUP_MASK(255,255,255,0);
WebServer server(80); DNSServer dns; WiFiUDP discovery; Preferences prefs; File uploadFile;
String savedSsid,savedPassword,setupSsid,deviceId,deviceName,managementKey,uploadPath,uploadTempPath,uploadBackupPath,uploadError;
bool restartPending=false; bool serverStarted=false; bool firmwareUploadOk=false; bool uploadOk=false; bool setupDnsActive=false; bool fileBatchActive=false; bool standaloneUsbUpdate=false; size_t uploadBytes=0; uint32_t restartAt=0,fileBatchTouchedAt=0;
bool wpsProvisioned=false,wpsActive=false; volatile uint8_t pendingWpsEvent=0,pendingWpsFailReason=0; uint32_t wpsStartedAt=0;

String makeDeviceId(){char v[10];snprintf(v,sizeof(v),"FT-%06X",(uint32_t)(ESP.getEfuseMac()&0xffffff));return String(v);}
String makeHostName(){String h="flyingthumb-"+deviceId.substring(3);h.toLowerCase();return h;}
String safePath(String p){p=server.urlDecode(p);if(!p.startsWith("/"))p="/"+p;if(p.indexOf("..")>=0||p.indexOf('\\')>=0)return String();return p;}
String uploadName(String n){int extended=n.indexOf("filename*=");if(extended>=0){n=n.substring(extended+10);int encoded=n.indexOf("''");if(encoded>=0)n=n.substring(encoded+2);}else{int extra=n.indexOf("\";");if(extra>=0)n=n.substring(0,extra);}n.replace("\"","");n.trim();return n;}
bool authorized(){return !managementKey.length()||(server.hasHeader("X-FlyingThumb-Key")&&server.header("X-FlyingThumb-Key")==managementKey);}
bool storageReady(){return SD_MMC.cardType()!=CARD_NONE&&SD_MMC.totalBytes()>0;}
String firmwareVersion(){const esp_partition_t*p=esp_ota_get_running_partition();String suffix="-?";if(p){String label=p->label;if(label=="app0")suffix="-A";else if(label=="app1")suffix="-B";}return String(FIRMWARE_VERSION_BASE)+suffix;}
void logMemory(const char* stage){Serial.printf("%s: firmware=%s heap=%u largest-internal=%u psram=%u free-psram=%u\n",stage,firmwareVersion().c_str(),ESP.getFreeHeap(),heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL|MALLOC_CAP_8BIT),ESP.getPsramSize(),ESP.getFreePsram());}
bool requireAuth(){if(authorized())return true;server.send(401,"application/json","{\"error\":\"invalid management key\"}");return false;}
void saveNetwork(const String&s,const String&p){prefs.begin("network",false);prefs.putString("ssid",s);prefs.putString("password",p);prefs.end();}
void saveDevice(){prefs.begin("device",false);prefs.putString("name",deviceName);prefs.putString("key",managementKey);prefs.end();}
void scheduleRestart(){restartPending=true;restartAt=millis()+900;}
void finishStandaloneUsbUpdate(){if(standaloneUsbUpdate){standaloneUsbUpdate=false;finishUsbFileUpdate();}}
void beginFileBatch(){if(!requireAuth())return;if(!storageReady()){server.send(503,"application/json","{\"error\":\"TF card unavailable\"}");return;}if(!fileBatchActive&&!beginUsbFileUpdate()){server.send(500,"application/json","{\"error\":\"USB storage could not enter managed mode\"}");return;}fileBatchActive=true;fileBatchTouchedAt=millis();server.send(200,"application/json","{\"status\":\"batch-ready\"}");}
void commitFileBatch(){if(!requireAuth())return;fileBatchActive=false;server.send(200,"application/json","{\"status\":\"usb-refreshing\"}");finishUsbFileUpdate();}
void releaseManagedUsb(){if(!requireAuth())return;if(fileBatchActive||usbFileUpdateActive()){server.send(409,"application/json","{\"error\":\"file update still active\"}");return;}if(!releaseUsbManagedMode()){server.send(500,"application/json","{\"error\":\"USB storage could not return to writable mode\"}");return;}server.send(200,"application/json","{\"status\":\"usb-writable\"}");}
void enableWifiPowerSave(){if(WiFi.getMode()==WIFI_STA||WiFi.getMode()==WIFI_AP_STA)esp_wifi_set_ps(WIFI_PS_MAX_MODEM);}
void showConnected(){String ip=WiFi.localIP().toString(),status="USB RW / "+firmwareVersion();displayMessage(deviceName.c_str(),ip.c_str(),status.c_str());}
void startDiscovery(){String h=makeHostName(),version=firmwareVersion();if(MDNS.begin(h.c_str())){MDNS.addService("flyingthumb","tcp",80);MDNS.addServiceTxt("flyingthumb","tcp","id",deviceId.c_str());MDNS.addServiceTxt("flyingthumb","tcp","name",deviceName.c_str());MDNS.addServiceTxt("flyingthumb","tcp","version",version.c_str());}discovery.begin(DISCOVERY_PORT);}
void startSetupAp(){setupSsid="FlyingThumb-"+deviceId.substring(3);WiFi.mode(WIFI_AP);WiFi.softAPConfig(SETUP_IP,SETUP_IP,SETUP_MASK);WiFi.softAP(setupSsid.c_str(),SETUP_PASSWORD);setupDnsActive=dns.start(DNS_PORT,"*",SETUP_IP);displayMessage("SETUP MODE",setupSsid.c_str(),"192.168.77.1");}
void onWifiEvent(WiFiEvent_t e,arduino_event_info_t info){if(e==ARDUINO_EVENT_WIFI_STA_GOT_IP){enableWifiPowerSave();showConnected();startDiscovery();}else if(e==ARDUINO_EVENT_WPS_ER_SUCCESS)pendingWpsEvent=1;else if(e==ARDUINO_EVENT_WPS_ER_FAILED){pendingWpsFailReason=(uint8_t)info.wps_fail_reason;pendingWpsEvent=2;}else if(e==ARDUINO_EVENT_WPS_ER_TIMEOUT)pendingWpsEvent=3;else if(e==ARDUINO_EVENT_WPS_ER_PBC_OVERLAP)pendingWpsEvent=4;}
void rememberWpsNetwork(){prefs.begin("network",false);prefs.clear();prefs.putBool("wps",true);prefs.end();savedSsid="";savedPassword="";wpsProvisioned=true;}
bool startWpsAttempt(){
  esp_wifi_wps_disable();logMemory("WPS attempt");
  esp_wps_config_t config=WPS_CONFIG_INIT_DEFAULT(WPS_TYPE_PBC);
  esp_err_t enableResult=esp_wifi_wps_enable(&config);
  esp_err_t startResult=enableResult==ESP_OK?esp_wifi_wps_start(0):enableResult;
  if(enableResult==ESP_OK&&startResult==ESP_OK){wpsActive=true;displayMessage("WPS ACTIVE","Press router","FW 2.4.7");Serial.println("WPS pairing attempt started");return true;}
  wpsActive=false;Serial.printf("WPS start failed: enable=0x%x (%s), start=0x%x (%s)\n",enableResult,esp_err_to_name(enableResult),startResult,esp_err_to_name(startResult));
  displayMessage("WPS FAILED","Use setup page","");return false;
}
void handleWpsState(){
  if(!wpsActive)return;
  if(millis()-wpsStartedAt>=WPS_PAIRING_WINDOW_MS){esp_wifi_wps_disable();wpsActive=false;displayMessage("WPS TIMED OUT","Short press","to retry");Serial.println("WPS pairing window expired");return;}
  uint8_t event=pendingWpsEvent;
  if(!event)return;
  pendingWpsEvent=0;esp_wifi_wps_disable();wpsActive=false;
  if(event==1){rememberWpsNetwork();displayMessage("WPS SUCCESS","Connecting...","");delay(10);WiFi.begin();return;}
  if(event==4){Serial.println("WPS PBC overlap: more than one router is advertising WPS");displayMessage("WPS OVERLAP","Only one router","can use WPS");return;}
  Serial.printf("WPS attempt ended: event=%u reason=%u\n",event,pendingWpsFailReason);
  displayMessage(event==3?"WPS TIMED OUT":"WPS FAILED","Short press","to retry");
}
void sendIndex(){if(WiFi.getMode()==WIFI_AP){server.send_P(200,"text/html",(const char*)settings_html_start,settings_html_end-settings_html_start);return;}server.send_P(200,"text/html",(const char*)index_html_start,index_html_end-index_html_start);}
void sendSettings(){server.sendHeader("Cache-Control","no-store");server.send_P(200,"text/html",(const char*)settings_html_start,settings_html_end-settings_html_start);}
void sendCaptivePortal(){server.sendHeader("Cache-Control","no-store");server.sendHeader("Location","http://192.168.77.1/",true);server.send(302,"text/plain","Open Flying Thumb Setup");}
void sendWindowsConnectTest(){server.sendHeader("Cache-Control","no-store");server.send(200,"text/plain","Microsoft Connect Test");}
void sendLegacyWindowsConnectTest(){server.sendHeader("Cache-Control","no-store");server.send(200,"text/plain","Microsoft NCSI");}
void fillInfo(JsonDocument&d){bool ready=storageReady();uint64_t total=ready?SD_MMC.totalBytes():0,used=ready?SD_MMC.usedBytes():0;d["service"]="flyingthumb";d["protocol"]=1;d["id"]=deviceId;d["name"]=deviceName;d["ip"]=WiFi.getMode()==WIFI_AP?WiFi.softAPIP().toString():WiFi.localIP().toString();d["port"]=80;d["firmware"]=firmwareVersion();d["storageReady"]=ready;d["storageTotal"]=total;d["storageFree"]=total-used;d["claimed"]=managementKey.length()>0;d["setupMode"]=WiFi.getMode()==WIFI_AP;d["usbManaged"]=usbManagedModeActive();}
void deviceInfo(){JsonDocument d;fillInfo(d);String j;serializeJson(d,j);server.send(200,"application/json",j);}
void listFiles(){if(!storageReady()){server.send(503,"application/json","{\"error\":\"TF card unavailable\"}");return;}String p=safePath(server.hasArg("dir")?server.arg("dir"):"/");if(!p.length()){server.send(400,"application/json","[]");return;}File root=SD_MMC.open(p);JsonDocument d;JsonArray a=d.to<JsonArray>();if(root&&root.isDirectory()){File f=root.openNextFile();while(f){if(f.name()[0]!='.'){JsonObject i=a.add<JsonObject>();i["type"]=f.isDirectory()?"dir":"file";i["name"]=f.name();i["size"]=f.size();}f.close();f=root.openNextFile();}}String j;serializeJson(d,j);server.send(200,"application/json",j);}
void diskInfo(){if(!storageReady()){server.send(503,"application/json","{\"error\":\"TF card unavailable\",\"storageReady\":false}");return;}JsonDocument d;d["storageReady"]=true;d["total"]=SD_MMC.totalBytes();d["used"]=SD_MMC.usedBytes();d["free"]=SD_MMC.totalBytes()-SD_MMC.usedBytes();d["cardType"]="SD";d["cardSize"]=SD_MMC.cardSize();String j;serializeJson(d,j);server.send(200,"application/json",j);}
void deleteFile(){if(!requireAuth())return;if(!storageReady()){server.send(503,"application/json","{\"error\":\"TF card unavailable\"}");return;}String p=safePath(server.arg("dir"));if(!p.length()||p=="/"){server.send(400,"text/plain","Invalid path");return;}if(!SD_MMC.exists(p)){server.send(404,"text/plain","Not found");return;}bool ownUsb=!fileBatchActive;if(ownUsb&&!beginUsbFileUpdate()){server.send(500,"application/json","{\"error\":\"USB storage could not enter managed mode\"}");return;}bool ok=SD_MMC.remove(p);server.send(ok?200:500,"application/json",ok?"{\"status\":\"usb-refreshing\"}":"{\"error\":\"delete failed\"}");if(ownUsb)finishUsbFileUpdate();}
bool restoreUploadBackup(){
  if(!uploadBackupPath.length()||!SD_MMC.exists(uploadBackupPath))return true;
  if(SD_MMC.exists(uploadPath))return SD_MMC.remove(uploadBackupPath);
  return SD_MMC.rename(uploadBackupPath,uploadPath);
}
bool commitUploadedFile(){
  if(SD_MMC.exists(uploadBackupPath)&&!SD_MMC.remove(uploadBackupPath)){uploadError="could not clear stale backup";return false;}
  const bool hadOld=SD_MMC.exists(uploadPath);
  if(hadOld&&!SD_MMC.rename(uploadPath,uploadBackupPath)){uploadError="could not preserve existing "+uploadPath;return false;}
  if(!SD_MMC.rename(uploadTempPath,uploadPath)){
    if(hadOld)SD_MMC.rename(uploadBackupPath,uploadPath);
    uploadError="could not commit "+uploadPath;
    return false;
  }
  File verify=SD_MMC.open(uploadPath,FILE_READ);
  const bool valid=verify&&verify.size()==uploadBytes;
  if(verify)verify.close();
  if(!valid){
    SD_MMC.remove(uploadPath);
    if(hadOld)SD_MMC.rename(uploadBackupPath,uploadPath);
    uploadError="committed file failed verification: "+uploadPath;
    return false;
  }
  if(hadOld)SD_MMC.remove(uploadBackupPath);
  return true;
}
void cleanupUploadArtifacts(){
  if(uploadFile)uploadFile.close();
  if(uploadTempPath.length()&&SD_MMC.exists(uploadTempPath))SD_MMC.remove(uploadTempPath);
  restoreUploadBackup();
}
void upload(){
  if(!authorized())return;
  HTTPUpload&r=server.upload();
  if(r.status==UPLOAD_FILE_START){
    uploadOk=false;uploadBytes=0;uploadError="";uploadPath=safePath(uploadName(r.filename));
    int slash=uploadPath.lastIndexOf('/');String dir=slash>=0?uploadPath.substring(0,slash+1):String("/");String base=slash>=0?uploadPath.substring(slash+1):uploadPath;
    uploadTempPath=dir+"."+base+".flyingthumb-new";uploadBackupPath=dir+"."+base+".flyingthumb-old";
    standaloneUsbUpdate=!fileBatchActive;
    if(standaloneUsbUpdate&&!beginUsbFileUpdate())uploadError="USB storage could not enter managed mode";
    if(fileBatchActive)fileBatchTouchedAt=millis();
    if(!storageReady())uploadError="TF card became unavailable";
    else if(!uploadPath.length()||uploadPath=="/")uploadError="invalid destination filename";
    else if(!uploadError.length()){
      restoreUploadBackup();
      if(SD_MMC.exists(uploadTempPath))SD_MMC.remove(uploadTempPath);
      uploadFile=SD_MMC.open(uploadTempPath,FILE_WRITE);
      uploadOk=bool(uploadFile);
      if(!uploadOk)uploadError="could not create temporary file for "+uploadPath;
    }
  }else if(r.status==UPLOAD_FILE_WRITE){
    if(fileBatchActive)fileBatchTouchedAt=millis();
    if(!uploadFile){uploadOk=false;if(!uploadError.length())uploadError="temporary file is not open";}
    else{
      size_t written=uploadFile.write(r.buf,r.currentSize);uploadBytes+=written;
      if(written!=r.currentSize){uploadOk=false;uploadError="short write to "+uploadPath+": "+String(written)+" of "+String(r.currentSize)+" bytes";}
    }
  }else if(r.status==UPLOAD_FILE_END){
    if(uploadFile)uploadFile.close();
    if(uploadBytes!=r.totalSize){uploadOk=false;if(!uploadError.length())uploadError="incomplete upload to "+uploadPath+": "+String(uploadBytes)+" of "+String(r.totalSize)+" bytes";}
    if(uploadOk){
      File verify=SD_MMC.open(uploadTempPath,FILE_READ);
      if(!verify||verify.size()!=uploadBytes){uploadOk=false;uploadError="temporary file failed verification: "+uploadPath;}
      if(verify)verify.close();
    }
    if(uploadOk)uploadOk=commitUploadedFile();
    if(!uploadOk)cleanupUploadArtifacts();
  }else if(r.status==UPLOAD_FILE_ABORTED){
    uploadOk=false;uploadError="upload was aborted";cleanupUploadArtifacts();
  }
}
void finishUpload(){if(!requireAuth()){finishStandaloneUsbUpdate();return;}if(!storageReady()){server.send(503,"application/json","{\"error\":\"TF card unavailable\"}");finishStandaloneUsbUpdate();return;}if(!uploadOk){JsonDocument d;d["error"]=uploadError.length()?uploadError:"file could not be written";String j;serializeJson(d,j);server.send(500,"application/json",j);finishStandaloneUsbUpdate();return;}server.send(200,"application/json",fileBatchActive?"{\"status\":\"uploaded\"}":"{\"status\":\"usb-refreshing\"}");finishStandaloneUsbUpdate();}
void firmwareUpload(){if(!authorized())return;HTTPUpload&r=server.upload();if(r.status==UPLOAD_FILE_START){firmwareUploadOk=Update.begin(UPDATE_SIZE_UNKNOWN);}else if(r.status==UPLOAD_FILE_WRITE){if(firmwareUploadOk&&Update.write(r.buf,r.currentSize)!=r.currentSize)firmwareUploadOk=false;}else if(r.status==UPLOAD_FILE_END){firmwareUploadOk=firmwareUploadOk&&Update.end(true);}else if(r.status==UPLOAD_FILE_ABORTED){Update.abort();firmwareUploadOk=false;}}
void finishFirmwareUpload(){if(!requireAuth())return;if(!firmwareUploadOk){server.send(500,"application/json","{\"error\":\"firmware validation or write failed\"}");return;}rememberOtaRequirements(storageReady());server.send(200,"application/json","{\"status\":\"upgrading\"}");scheduleRestart();}
void restartDevice(){if(!requireAuth())return;server.send(200,"application/json","{\"status\":\"restarting\"}");scheduleRestart();}
void saveSetup(){if(WiFi.getMode()!=WIFI_AP&&!requireAuth())return;if(!server.hasArg("plain")){server.send(400,"application/json","{\"error\":\"missing body\"}");return;}JsonDocument d;if(deserializeJson(d,server.arg("plain"))){server.send(400,"application/json","{\"error\":\"invalid JSON\"}");return;}String ssid=d["ssid"]|"",pass=d["password"]|"",name=d["name"]|deviceName,key=d["key"]|managementKey;ssid.trim();name.trim();key.trim();if(!ssid.length()){server.send(400,"application/json","{\"error\":\"Wi-Fi name required\"}");return;}if(!name.length())name=deviceId;deviceName=name.substring(0,31);managementKey=key.substring(0,63);saveDevice();saveNetwork(ssid,pass);server.send(200,"application/json","{\"status\":\"saved\"}");scheduleRestart();}
void updateDevice(){if(!requireAuth())return;JsonDocument d;if(!server.hasArg("plain")||deserializeJson(d,server.arg("plain"))){server.send(400,"application/json","{\"error\":\"invalid JSON\"}");return;}String name=d["name"]|deviceName,key=d["key"]|managementKey;name.trim();key.trim();if(name.length())deviceName=name.substring(0,31);managementKey=key.substring(0,63);saveDevice();server.send(200,"application/json","{\"status\":\"saved\"}");scheduleRestart();}
void downloadOr404(){String p=safePath(server.uri());if(p=="/"||!p.length()){sendIndex();return;}if(p=="/settings.html"){sendSettings();return;}if(WiFi.getMode()==WIFI_AP){sendCaptivePortal();return;}if(p.length()&&SD_MMC.exists(p)){File f=SD_MMC.open(p,FILE_READ);server.streamFile(f,"application/octet-stream");f.close();return;}server.send(404,"text/plain","Not found");}
void startServer(){const char*h[]={"X-FlyingThumb-Key"};server.collectHeaders(h,1);server.on("/",HTTP_GET,sendIndex);server.on("/settings.html",HTTP_GET,sendSettings);server.on("/connecttest.txt",HTTP_GET,sendWindowsConnectTest);server.on("/ncsi.txt",HTTP_GET,sendLegacyWindowsConnectTest);server.on("/redirect",HTTP_GET,sendCaptivePortal);server.on("/generate_204",HTTP_GET,sendCaptivePortal);server.on("/gen_204",HTTP_GET,sendCaptivePortal);server.on("/hotspot-detect.html",HTTP_GET,sendCaptivePortal);server.on("/library/test/success.html",HTTP_GET,sendCaptivePortal);server.on("/api/device",HTTP_GET,deviceInfo);server.on("/api/device",HTTP_POST,updateDevice);server.on("/api/list",HTTP_GET,listFiles);server.on("/api/disk",HTTP_GET,diskInfo);server.on("/api/setup",HTTP_POST,saveSetup);server.on("/api/files/begin",HTTP_POST,beginFileBatch);server.on("/api/files/commit",HTTP_POST,commitFileBatch);server.on("/api/files/release",HTTP_POST,releaseManagedUsb);server.on("/delete",HTTP_POST,deleteFile);server.on("/upload",HTTP_POST,finishUpload,upload);server.on("/api/firmware",HTTP_POST,finishFirmwareUpload,firmwareUpload);server.on("/api/restart",HTTP_POST,restartDevice);server.onNotFound(downloadOr404);server.begin();serverStarted=true;}
void handleDiscovery(){int n=discovery.parsePacket();if(!n)return;char q[64]={};int len=discovery.read(q,sizeof(q)-1);if(len<=0||String(q)!=DISCOVERY_REQUEST)return;JsonDocument d;fillInfo(d);String j;serializeJson(d,j);discovery.beginPacket(discovery.remoteIP(),discovery.remotePort());discovery.write((const uint8_t*)j.c_str(),j.length());discovery.endPacket();}
}

void clearNetworkSettings(){prefs.begin("network",false);prefs.clear();prefs.end();wpsProvisioned=false;WiFi.disconnect(true,true);}
void beginWpsPairing(){
  pendingWpsEvent=0;wpsActive=false;wpsStartedAt=millis();
  if(setupDnsActive){dns.stop();setupDnsActive=false;}
  MDNS.end();discovery.stop();
  WiFi.mode(WIFI_STA);WiFi.disconnect();delay(100);esp_wifi_set_ps(WIFI_PS_NONE);
  startWpsAttempt();
}
void initNetworkAndServer(){deviceId=makeDeviceId();logMemory("Network startup");prefs.begin("device",true);deviceName=prefs.getString("name",deviceId);managementKey=prefs.getString("key","");prefs.end();prefs.begin("network",true);savedSsid=prefs.getString("ssid","");savedPassword=prefs.getString("password","");wpsProvisioned=prefs.getBool("wps",false);prefs.end();WiFi.onEvent(onWifiEvent);if(!savedSsid.length()&&!wpsProvisioned){startSetupAp();startDiscovery();}else{WiFi.mode(WIFI_STA);WiFi.setHostname(makeHostName().c_str());if(wpsProvisioned)WiFi.begin();else WiFi.begin(savedSsid.c_str(),savedPassword.c_str());displayMessage(deviceName.c_str(),"Connecting...","");uint32_t start=millis();while(WiFi.status()!=WL_CONNECTED&&millis()-start<STA_CONNECT_TIMEOUT_MS)delay(100);if(WiFi.status()==WL_CONNECTED)showConnected();else displayMessage("WIFI OFFLINE","Short: WPS","Hold: reset");}startServer();}
void handleNetworkAndServer(){handleWpsState();if(setupDnsActive)dns.processNextRequest();if(serverStarted)server.handleClient();handleDiscovery();if(fileBatchActive&&millis()-fileBatchTouchedAt>120000){fileBatchActive=false;finishUsbFileUpdate();}if(restartPending&&millis()>=restartAt)ESP.restart();}
