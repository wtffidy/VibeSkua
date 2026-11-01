package {
    import flash.display.Sprite;
    import flash.events.DataEvent;
    import flash.events.Event;
    import flash.events.IOErrorEvent;
    import flash.external.ExternalInterface;
    import flash.net.XMLSocket;

    [SWF(width="960", height="540", frameRate="30", backgroundColor="#202020")]
    public class RuffleBridgeSmoke extends Sprite {
        private var _socket:XMLSocket;

        public function RuffleBridgeSmoke() {
            ExternalInterface.addCallback("isTrue", isTrue);
            ExternalInterface.addCallback("connectLocalSocket", connectLocalSocket);
        }

        public function isTrue():String {
            ExternalInterface.call("bridgeSmoke", "ready");
            return "true";
        }

        public function connectLocalSocket(host:String, port:int):String {
            _socket = new XMLSocket();
            _socket.addEventListener(Event.CONNECT, onSocketConnected);
            _socket.addEventListener(DataEvent.DATA, onSocketData);
            _socket.addEventListener(IOErrorEvent.IO_ERROR, onSocketError);
            _socket.connect(host, port);
            return "connecting";
        }

        private function onSocketConnected(event:Event):void {
            _socket.send("VibeSkua Ruffle TCP smoke");
            ExternalInterface.call("socketConnected");
        }

        private function onSocketData(event:DataEvent):void {
            ExternalInterface.call("socketData", event.data);
        }

        private function onSocketError(event:IOErrorEvent):void {
            ExternalInterface.call("socketError", event.text);
        }
    }
}
