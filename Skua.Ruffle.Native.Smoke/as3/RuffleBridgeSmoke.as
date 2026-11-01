package {
    import flash.display.Sprite;
    import flash.external.ExternalInterface;

    [SWF(width="960", height="540", frameRate="30", backgroundColor="#202020")]
    public class RuffleBridgeSmoke extends Sprite {
        public function RuffleBridgeSmoke() {
            ExternalInterface.addCallback("isTrue", isTrue);
        }

        public function isTrue():String {
            ExternalInterface.call("bridgeSmoke", "ready");
            return "true";
        }
    }
}
