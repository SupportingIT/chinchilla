using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Chinchilla.Topologies.Model;
using RabbitMQ.Client;

namespace Chinchilla
{
    public class Publisher : Trackable { }

    public class Publisher<TMessage> : Publisher, IPublisher<TMessage>
    {
        private readonly IRouter router;

        private readonly IMessageSerializer serializer;

        private IHeadersStrategy<TMessage> headerStrategy;

        protected bool disposed;

        private long numPublishedMessages;

        public Publisher(
            IModelReference modelReference,
            IMessageSerializer serializer,
            IExchange exchange,
            IRouter router,
            IHeadersStrategy<TMessage> headerStrategy)
        {
            this.serializer = Guard.NotNull(serializer, "serializer");
            this.router = Guard.NotNull(router, "router");
            this.headerStrategy = Guard.NotNull(headerStrategy, "headerStrategy");

            ModelReference = Guard.NotNull(modelReference, "modelReference");
            Exchange = Guard.NotNull(exchange, "bindable");
        }

        public IModelReference ModelReference { get; private set; }

        public IExchange Exchange { get; private set; }

        public long NumPublishedMessages
        {
            get { return numPublishedMessages; }
        }

        public virtual void Start()
        {

        }

        public IPublishReceipt Publish(TMessage message)
        {
            var wrappedMessage = Message.Create(message);
            var serializedMessage = serializer.Serialize(wrappedMessage);
            var routingKey = router.Route(message);

            if (routingKey == null)
            {
                var exceptionMessage = string.Format(
                    "An error occured while trying to publish a message of type {0} because it has a null " +
                    "routing key, this could be because IHasRoutingKey implemented, but the RoutingKey property " +
                    "returned null.", typeof(TMessage).Name);

                throw new ChinchillaException(exceptionMessage);
            }

            var publishReceipt = ModelReference.Execute(model =>
            {
                var defaultProperties = CreateProperties(message);

                return PublishWithReceipt(
                    message,
                    model,
                    routingKey,
                    defaultProperties,
                    serializedMessage);
            });

            Interlocked.Increment(ref numPublishedMessages);

            return publishReceipt;
        }

        public virtual IPublishReceipt PublishWithReceipt(
            TMessage originalMessage,
            IModel model,
            string routingKey,
            IBasicProperties defaultProperties,
            byte[] serializedMessage)
        {
            model.BasicPublish(
                Exchange.Name,
                routingKey,
                defaultProperties,
                serializedMessage);

            return NullReceipt.Instance;
        }

        public IBasicProperties CreateProperties(TMessage message)
        {
            var defaultProperties = ModelReference.Execute(m => m.CreateBasicProperties());
            defaultProperties.ContentType = serializer.ContentType;

            var correlated = message as ICorrelated;
            if (correlated != null)
            {
                defaultProperties.CorrelationId = correlated.CorrelationId.ToString();
            }

            var timeout = message as IHasTimeOut;
            if (timeout != null)
            {
                var formattedExpiration = timeout.Timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
                defaultProperties.Expiration = formattedExpiration;
            }

            var hasHeaders = message as IHasHeaders;
            if (hasHeaders != null)
            {
                var headers = new Dictionary<object, object>();
                hasHeaders.PopulateHeaders(headers);
                defaultProperties.Headers = headers;
            }

            defaultProperties.SetPersistent(!(message is ITransient));

            var replyTo = router.ReplyTo();
            if (!string.IsNullOrEmpty(replyTo))
            {
                defaultProperties.ReplyTo = router.ReplyTo();
            }

            return defaultProperties;
        }

        public override void Dispose()
        {
            if (disposed)
            {
                return;
            }

            ModelReference.Dispose();
            disposed = true;
        }
    }
}